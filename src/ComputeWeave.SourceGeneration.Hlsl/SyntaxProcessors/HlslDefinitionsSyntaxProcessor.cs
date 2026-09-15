using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using ComputeWeave.SourceGeneration.Extensions;
using ComputeWeave.SourceGeneration.Helpers;
using ComputeWeave.SourceGeneration.Mappings;
using ComputeWeave.SourceGeneration.Models;
using ComputeWeave.SourceGeneration.SyntaxRewriters;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static ComputeWeave.SourceGeneration.Diagnostics.DiagnosticDescriptors;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace ComputeWeave.SourceGeneration.SyntaxProcessors;

/// <summary>
/// A processor responsible for extracting definitions from shader types.
/// </summary>
internal static class HlslDefinitionsSyntaxProcessor
{
    /// <summary>
    /// Gets a sequence of discovered constants.
    /// </summary>
    /// <param name="constantDefinitions">The collection of discovered constant definitions.</param>
    /// <returns>A sequence of discovered constants to declare in the shader.</returns>
    public static ImmutableArray<HlslConstant> GetDefinedConstants(IReadOnlyDictionary<IFieldSymbol, string> constantDefinitions)
    {
        using ImmutableArrayBuilder<HlslConstant> builder = new();

        foreach (KeyValuePair<IFieldSymbol, string> constant in constantDefinitions)
        {
            string ownerTypeName = ((INamedTypeSymbol)constant.Key.ContainingSymbol).ToDisplayString().ToHlslIdentifierName();
            string constantName = $"__{ownerTypeName}__{constant.Key.Name}";

            builder.Add((constantName, constant.Value));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Tries to get and rewrite a given static field to be used in a shader.
    /// </summary>
    /// <param name="structDeclarationSymbol">The type symbol for the shader type.</param>
    /// <param name="fieldSymbol">The symbol for the field to analyze.</param>
    /// <param name="semanticModel">The <see cref="SemanticModelProvider"/> instance for the type to process.</param>
    /// <param name="discoveredTypes">The collection of currently discovered types.</param>
    /// <param name="staticMethods">The collection of discovered static methods.</param>
    /// <param name="instanceMethods">The collection of discovered instance methods for custom struct types.</param>
    /// <param name="constructors">The collection of discovered constructors for custom struct types.</param>
    /// <param name="constantDefinitions">The collection of discovered constant definitions.</param>
    /// <param name="staticFieldDefinitions">The collection of discovered static field definitions.</param>
    /// <param name="requirements">The requirements gathered for the shader being rewritten.</param>
    /// <param name="calls">The collection of calls the generated HLSL holds, recorded from the declarations they are written in.</param>
    /// <param name="diagnostics">The collection of produced <see cref="DiagnosticInfo"/> instances.</param>
    /// <param name="token">The <see cref="CancellationToken"/> used to cancel the operation, if needed.</param>
    /// <param name="name">The mapped name for the field.</param>
    /// <param name="typeDeclaration">The type declaration for the field.</param>
    /// <param name="assignmentExpression">The assignment expression for the field, if present.</param>
    /// <param name="staticFieldRewriter">The <see cref="StaticFieldRewriter"/> instance used to rewrite the field expression.</param>
    /// <returns>Whether the field was processed successfully and is valid.</returns>
    public static bool TryGetStaticField(
        INamedTypeSymbol structDeclarationSymbol,
        IFieldSymbol fieldSymbol,
        SemanticModelProvider semanticModel,
        ICollection<INamedTypeSymbol> discoveredTypes,
        IDictionary<IMethodSymbol, MethodDeclarationSyntax> staticMethods,
        IDictionary<IMethodSymbol, MethodDeclarationSyntax> instanceMethods,
        IDictionary<IMethodSymbol, (MethodDeclarationSyntax, MethodDeclarationSyntax)> constructors,
        IDictionary<IFieldSymbol, string> constantDefinitions,
        IDictionary<IFieldSymbol, HlslStaticField> staticFieldDefinitions,
        HlslShaderRequirements requirements,
        ICollection<HlslCall> calls,
        ImmutableArrayBuilder<DiagnosticInfo> diagnostics,
        CancellationToken token,
        [NotNullWhen(true)] out string? name,
        [NotNullWhen(true)] out string? typeDeclaration,
        out string? assignmentExpression,
        [NotNullWhen(true)] out StaticFieldRewriter? staticFieldRewriter)
    {
        if (fieldSymbol.IsImplicitlyDeclared || !fieldSymbol.IsStatic || fieldSymbol.IsConst)
        {
            goto Failure;
        }

        if (!fieldSymbol.TryGetSyntaxNode(token, out VariableDeclaratorSyntax? variableDeclarator))
        {
            goto Failure;
        }

        // Static fields must be of a primitive, vector or matrix type
        if (fieldSymbol.Type is not INamedTypeSymbol typeSymbol ||
            !HlslKnownTypes.IsKnownHlslType(typeSymbol.GetFullyQualifiedMetadataName()))
        {
            diagnostics.Add(InvalidShaderStaticFieldType, variableDeclarator, structDeclarationSymbol, fieldSymbol.Name, fieldSymbol.Type);

            goto Failure;
        }

        _ = HlslKnownKeywords.TryGetMappedName(fieldSymbol.Name, out string? mapping);

        // The field name is either the mapped name (if a reserved name) or just the field name.
        // This method is shared across external fields too, and callers can just override this.
        name = mapping ?? fieldSymbol.Name;

        // Readonly fields are rewritten to static const fields, and mutable fields are just static.
        // Note that there's no protection for mutable static fields that may have been written to
        // in C# elsewhere. Shader authors should be aware that those writes would not appear in HLSL,
        // as each shader invocation would only see the initial assignment value (or the default value).
        typeDeclaration = fieldSymbol.IsReadOnly switch
        {
            true => $"static const {HlslKnownTypes.GetMappedName(typeSymbol)}",
            false => $"static {HlslKnownTypes.GetMappedName(typeSymbol)}"
        };

        token.ThrowIfCancellationRequested();

        // Create the rewriter to use, which is also returned to callers so they can extract the local
        // functions an initializer lifted out. What the initializer requires of the shader is raised
        // into the shared requirements instead, so a caller has nothing to read back out for that.
        staticFieldRewriter = new StaticFieldRewriter(
            structDeclarationSymbol,
            semanticModel,
            discoveredTypes,
            staticMethods,
            instanceMethods,
            constructors,
            constantDefinitions,
            staticFieldDefinitions,
            requirements,
            calls,
            diagnostics,
            token);

        ExpressionSyntax? processedDeclaration = staticFieldRewriter.Visit(variableDeclarator);

        token.ThrowIfCancellationRequested();

        assignmentExpression = processedDeclaration?.NormalizeWhitespace(eol: "\n").ToFullString();

        return true;

        Failure:
        name = null;
        typeDeclaration = null;
        assignmentExpression = null;
        staticFieldRewriter = null;

        return false;
    }

    /// <summary>
    /// Gets the sequence of processed discovered custom types.
    /// </summary>
    /// <param name="diagnostics">The collection of produced <see cref="DiagnosticInfo"/> instances.</param>
    /// <param name="structDeclarationSymbol">The type symbol for the shader type.</param>
    /// <param name="types">The sequence of discovered custom types.</param>
    /// <param name="instanceMethods">The collection of discovered instance methods for custom struct types.</param>
    /// <param name="constructors">The collection of discovered constructors for custom struct types.</param>
    /// <param name="typeDeclarations">The collection of declarations of all custom types, in a valid HLSL declaration order.</param>
    /// <param name="forwardDeclarations">The member method prototypes naming a custom type ahead of its declaration, with that type.</param>
    /// <param name="methodDeclarations">The collection of implementations of all methods in all custom types.</param>
    public static void GetDeclaredTypes(
        ImmutableArrayBuilder<DiagnosticInfo> diagnostics,
        INamedTypeSymbol structDeclarationSymbol,
        IEnumerable<INamedTypeSymbol> types,
        IReadOnlyDictionary<IMethodSymbol, MethodDeclarationSyntax> instanceMethods,
        IReadOnlyDictionary<IMethodSymbol, (MethodDeclarationSyntax, MethodDeclarationSyntax)> constructors,
        out ImmutableArray<HlslUserType> typeDeclarations,
        out ImmutableArray<(IMethodSymbol Prototype, INamedTypeSymbol Type)> forwardDeclarations,
        out ImmutableArray<string> methodDeclarations)
    {
        using ImmutableArrayBuilder<HlslUserType> typeDeclarationsBuilder = new();
        using ImmutableArrayBuilder<string> methodDeclarationsBuilder = new();

        IReadOnlyCollection<INamedTypeSymbol> invalidTypes;

        // Process the discovered types. The prototypes a declaration holds are the ones gathered below, so
        // the same methods and constructors are what the declaration order is resolved over.
        foreach (INamedTypeSymbol type in HlslKnownTypes.GetCustomTypes(types, instanceMethods.Keys.Concat(constructors.Keys), out invalidTypes, out forwardDeclarations))
        {
            string structType = type.GetFullyQualifiedMetadataName().ToHlslIdentifierName();
            StructDeclarationSyntax structDeclaration = StructDeclaration(structType);

            // Declare the fields of the current type
            foreach (ISymbol memberSymbol in type.GetMembers())
            {
                // Once again, skip constants and static fields
                if (memberSymbol is not IFieldSymbol { IsConst: false, IsStatic: false } fieldSymbol)
                {
                    continue;
                }

                // Try to get the actual field name
                if (!ConstantBufferSyntaxProcessor.TryGetFieldAccessorName(fieldSymbol, out string? fieldName, out _))
                {
                    continue;
                }

                INamedTypeSymbol fieldType = (INamedTypeSymbol)fieldSymbol.Type;

                // Convert the name to the fully qualified HLSL version
                if (!HlslKnownTypes.TryGetMappedName(fieldType.GetFullyQualifiedMetadataName(), out string? mappedType))
                {
                    mappedType = fieldType.GetFullyQualifiedMetadataName().ToHlslIdentifierName();
                }

                // Get the field name as a valid HLSL identifier
                if (!HlslKnownKeywords.TryGetMappedName(fieldName, out string? mappedName))
                {
                    mappedName = fieldName;
                }

                structDeclaration = structDeclaration.AddMembers(
                    FieldDeclaration(VariableDeclaration(
                        IdentifierName(mappedType)).AddVariables(
                        VariableDeclarator(Identifier(mappedName!)))));
            }

            // Enumerate all members in a single pass, so we can avoid materializing the collection.
            // Additionally, this lets us add all members to the struct declaration in a single go.
            static IEnumerable<MethodDeclarationSyntax> GatherInstanceMethods(
                INamedTypeSymbol type,
                IReadOnlyDictionary<IMethodSymbol, MethodDeclarationSyntax> instanceMethods,
                IReadOnlyDictionary<IMethodSymbol, (MethodDeclarationSyntax, MethodDeclarationSyntax)> constructors)
            {
                // Normal instance methods
                foreach (KeyValuePair<IMethodSymbol, MethodDeclarationSyntax> method in instanceMethods)
                {
                    if (SymbolEqualityComparer.Default.Equals(method.Key.ContainingType, type))
                    {
                        yield return method.Value;
                    }
                }

                // Constructors and stubs
                foreach (KeyValuePair<IMethodSymbol, (MethodDeclarationSyntax Stub, MethodDeclarationSyntax Ctor)> methods in constructors)
                {
                    if (SymbolEqualityComparer.Default.Equals(methods.Key.ContainingType, type))
                    {
                        yield return methods.Value.Stub;
                        yield return methods.Value.Ctor;
                    }
                }
            }

            using (ImmutableArrayBuilder<MethodDeclarationSyntax> methodDefinitions = new())
            {
                // Forward declarations for all methods, and implementations.
                // We need these to ensure things work with arbitrary ordering.
                foreach (MethodDeclarationSyntax methodDeclaration in GatherInstanceMethods(type, instanceMethods, constructors))
                {
                    methodDefinitions.Add(methodDeclaration.AsDefinition());

                    // We need to normalize the whitespaces here, as this methhod declaration will not be added to
                    // the list of members for the struct declaration, but rather it will be written directly into
                    // the resulting HLSL source (as the implementation of this forward declaration). For the same
                    // reason, we also need to change the identifier to also include the containing type with '::'.
                    // Parameter default values stay on the forward declaration only, as HLSL rejects repeating them
                    string methodImplementation =
                        methodDeclaration
                        .WithoutParameterDefaults()
                        .WithIdentifier(Identifier($"{structType}::{methodDeclaration.Identifier.Text}"))
                        .NormalizeWhitespace(eol: "\n")
                        .ToFullString();

                    methodDeclarationsBuilder.Add(methodImplementation);
                }

                // Add all method forward declarations to the current type
                structDeclaration = structDeclaration.WithMembers(structDeclaration.Members.AddRange(methodDefinitions.AsEnumerable()));
            }

            // Insert the trailing ; right after the closing bracket (after normalization)
            typeDeclarationsBuilder.Add((
                structType,
                structDeclaration
                    .NormalizeWhitespace(eol: "\n")
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                    .ToFullString()));
        }

        // Process the invalid types
        foreach (INamedTypeSymbol invalidType in invalidTypes)
        {
            diagnostics.Add(InvalidDiscoveredType, structDeclarationSymbol, structDeclarationSymbol, invalidType);
        }

        typeDeclarations = typeDeclarationsBuilder.ToImmutable();
        methodDeclarations = methodDeclarationsBuilder.ToImmutable();
    }

    /// <summary>
    /// Finds and reports all invalid declared properties in a shader.
    /// </summary>
    /// <param name="diagnostics">The collection of produced <see cref="DiagnosticInfo"/> instances.</param>
    /// <param name="structDeclarationSymbol">The input <see cref="INamedTypeSymbol"/> instance to process.</param>
    public static void DetectAndReportInvalidPropertyDeclarations(ImmutableArrayBuilder<DiagnosticInfo> diagnostics, INamedTypeSymbol structDeclarationSymbol)
    {
        foreach (ISymbol memberSymbol in structDeclarationSymbol.GetMembers())
        {
            // Detect properties that are not explicit interface implementations
            if (memberSymbol is IPropertySymbol { ExplicitInterfaceImplementations.IsEmpty: true })
            {
                diagnostics.Add(InvalidPropertyDeclaration, memberSymbol, structDeclarationSymbol, memberSymbol);
            }

            // Detect properties causing a field to be generated
            if (memberSymbol is IFieldSymbol { AssociatedSymbol: IPropertySymbol associatedProperty })
            {
                diagnostics.Add(InvalidPropertyDeclaration, associatedProperty, structDeclarationSymbol, associatedProperty);
            }
        }
    }

    /// <summary>
    /// Reports every call that leads back to the declaration it is written in.
    /// </summary>
    /// <param name="diagnostics">The collection of produced <see cref="DiagnosticInfo"/> instances.</param>
    /// <param name="calls">The collection of calls the generated HLSL holds, recorded from the declarations they are written in.</param>
    /// <remarks>
    /// <para>
    /// HLSL has no recursion, so the shader compiler refuses a function that reaches itself through the
    /// functions it calls. Whether a call does is a property of every declaration the generated HLSL holds,
    /// so this runs once every declaration is rewritten, over the calls each rewriting recorded: a method
    /// of the shader, a method or constructor of another type the shader reaches, and a local function are
    /// all written out as functions, a local function whether or not it is called.
    /// </para>
    /// <para>
    /// Every call on a cycle is reported, at the call as the author wrote it, so a cycle through two
    /// declarations names both of the calls closing it and either one can be the one removed.
    /// </para>
    /// </remarks>
    public static void ReportRecursiveCalls(ImmutableArrayBuilder<DiagnosticInfo> diagnostics, IReadOnlyCollection<HlslCall> calls)
    {
        Dictionary<IMethodSymbol, List<IMethodSymbol>> callees = new(SymbolEqualityComparer.Default);

        foreach ((IMethodSymbol caller, IMethodSymbol callee, _) in calls)
        {
            if (!callees.TryGetValue(caller, out List<IMethodSymbol>? targets))
            {
                targets = [];

                callees.Add(caller, targets);
            }

            targets.Add(callee);
        }

        // The declarations a call leads to, following the calls those hold in turn
        Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> reachable = new(SymbolEqualityComparer.Default);

        HashSet<IMethodSymbol> GetReachable(IMethodSymbol callee)
        {
            if (!reachable.TryGetValue(callee, out HashSet<IMethodSymbol>? reached))
            {
                reached = new(SymbolEqualityComparer.Default);

                Stack<IMethodSymbol> pending = new([callee]);

                while (pending.Count > 0)
                {
                    if (callees.TryGetValue(pending.Pop(), out List<IMethodSymbol>? next))
                    {
                        foreach (IMethodSymbol declaration in next)
                        {
                            if (reached.Add(declaration))
                            {
                                pending.Push(declaration);
                            }
                        }
                    }
                }

                reachable.Add(callee, reached);
            }

            return reached;
        }

        foreach ((IMethodSymbol caller, IMethodSymbol callee, SyntaxNode site) in calls)
        {
            if (GetReachable(callee).Contains(caller))
            {
                diagnostics.Add(RecursiveCall, site, callee, caller);
            }
        }
    }

    /// <summary>
    /// Gets the order the next static field to finish takes, which is how many have finished before it.
    /// </summary>
    /// <param name="staticFieldDefinitions">The collection of discovered static field definitions.</param>
    /// <returns>The number of imported static fields whose rewriting has finished.</returns>
    /// <remarks>
    /// Writing the fields in this order puts each one after every field that had finished when it did,
    /// which is every field its own initializer reached. An entry with no type declaration is one still
    /// being rewritten, so it is not one that has finished.
    /// </remarks>
    public static int GetStaticFieldOrder(IDictionary<IFieldSymbol, HlslStaticField> staticFieldDefinitions)
    {
        int order = 0;

        foreach (HlslStaticField definition in staticFieldDefinitions.Values)
        {
            if (definition.TypeDeclaration is not null)
            {
                order++;
            }
        }

        return order;
    }

    /// <summary>
    /// Reports every access to a static field that C# performs before the initializer of that field has run.
    /// </summary>
    /// <param name="structDeclarationSymbol">The type symbol for the shader type.</param>
    /// <param name="staticFieldDefinitions">The collection of discovered static field definitions.</param>
    /// <param name="semanticModel">The <see cref="SemanticModelProvider"/> instance for the type to process.</param>
    /// <param name="diagnostics">The collection of produced <see cref="DiagnosticInfo"/> instances.</param>
    /// <param name="token">The <see cref="CancellationToken"/> used to cancel the operation, if needed.</param>
    /// <remarks>
    /// <para>
    /// C# runs the static field initializers of a type once, in the order the fields are declared. A field of
    /// that type whose initializer has not run yet holds the default value of its type, so a read of it from the
    /// initializer being run, or from anything that initializer reaches, is that default, and a write to it is
    /// discarded when its initializer runs. The generated HLSL reproduces neither: the shader compiler folds
    /// the initializer the field carries when it can, so the read is the initialized value and the write stays,
    /// leaves the read undefined otherwise, and does not compile a direct read of a global declared later. The
    /// field being initialized is the first one whose turn has not come, so an initializer reaching back into
    /// itself is the same case, except for a write, which the initializer overwrites in the generated HLSL as
    /// well. A field carrying no initializer holds the default value in both and is not part of this.
    /// </para>
    /// <para>
    /// The walk starts from the initializer of every static field the generated HLSL declares, the ones of the
    /// shader and the imported ones alike, and follows every declaration it reaches: the method, local function
    /// or constructor a call resolves to, and the initializer of a static field of another type, whose
    /// initializers C# runs when that type is first touched. A field of the same type is not walked into, its
    /// initializer having run already or not running until its own turn, and a local function is walked only
    /// through a call, C# not running one that is never called. An access two initializers both perform too
    /// early is one place to change, so it is reported once, for the first of the two in declaration order.
    /// The walk does not follow the order of the statements it passes, so a read after a write in the same
    /// declaration is reported like any other.
    /// </para>
    /// <para>
    /// This runs once after the initializers are rewritten rather than as they are, because a rewriting does
    /// not pass through every declaration an initializer reaches: a declaration is imported once, so one the
    /// body or an earlier initializer imported is not rewritten again, and a method of the shader is never
    /// imported. Semantic information is resolved only for the kinds an access, a call or a construction can
    /// be written as, an access written as a member access resolving on the access alone.
    /// </para>
    /// </remarks>
    public static void ReportStaticFieldAccessesBeforeInitialization(
        INamedTypeSymbol structDeclarationSymbol,
        IDictionary<IFieldSymbol, HlslStaticField> staticFieldDefinitions,
        SemanticModelProvider semanticModel,
        ImmutableArrayBuilder<DiagnosticInfo> diagnostics,
        CancellationToken token)
    {
        Dictionary<INamedTypeSymbol, List<(IFieldSymbol Field, ExpressionSyntax Initializer)>> initializers = new(SymbolEqualityComparer.Default);

        // An access is one place the author has to change, so one reached from two initializers is reported
        // once, naming the first of the two in declaration order
        HashSet<SyntaxNode> reportedAccesses = [];

        // The static fields of a type that carry an initializer, in the order C# runs those
        List<(IFieldSymbol Field, ExpressionSyntax Initializer)> GetInitializers(INamedTypeSymbol type)
        {
            if (!initializers.TryGetValue(type, out List<(IFieldSymbol Field, ExpressionSyntax Initializer)>? order))
            {
                order = [];

                foreach (ISymbol member in type.GetMembers())
                {
                    if (member is IFieldSymbol { IsImplicitlyDeclared: false, IsStatic: true, IsConst: false } field &&
                        field.TryGetSyntaxNode(token, out VariableDeclaratorSyntax? declarator) &&
                        declarator.Initializer is { } initializer)
                    {
                        order.Add((field, initializer.Value));
                    }
                }

                initializers.Add(type, order);
            }

            return order;
        }

        // A simple assignment to the field and an out argument write it without reading it
        static bool IsWrite(IFieldReferenceOperation reference)
        {
            return reference.Parent switch
            {
                ISimpleAssignmentOperation assignment => ReferenceEquals(assignment.Target, reference),
                IArgumentOperation { Parameter.RefKind: RefKind.Out } => true,
                _ => false
            };
        }

        // The imported fields are walked in the declaration order of their type too, so that an access two of
        // them perform too early names the first of the two
        HashSet<IFieldSymbol> importedFields = new(staticFieldDefinitions.Keys, SymbolEqualityComparer.Default);
        HashSet<INamedTypeSymbol> importedTypes = new(SymbolEqualityComparer.Default);
        List<(IFieldSymbol Field, ExpressionSyntax Initializer)> roots = [.. GetInitializers(structDeclarationSymbol)];

        foreach (IFieldSymbol importedField in staticFieldDefinitions.Keys)
        {
            if (importedTypes.Add(importedField.ContainingType))
            {
                roots.AddRange(GetInitializers(importedField.ContainingType).Where(root => importedFields.Contains(root.Field)));
            }
        }

        foreach ((IFieldSymbol field, ExpressionSyntax initializer) in roots)
        {
            token.ThrowIfCancellationRequested();

            // The fields of the type whose initializer has not run when this one runs: this field and the ones after it
            HashSet<IFieldSymbol> pending = new(
                GetInitializers(field.ContainingType).SkipWhile(candidate => !SymbolEqualityComparer.Default.Equals(candidate.Field, field)).Select(candidate => candidate.Field),
                SymbolEqualityComparer.Default);
            HashSet<ISymbol> visited = new(SymbolEqualityComparer.Default);

            WalkReachedNodes(initializer);

            void WalkReachedDeclaration(ISymbol symbol, SyntaxNode? root)
            {
                if (root is not null && visited.Add(symbol))
                {
                    WalkReachedNodes(root);
                }
            }

            void WalkReachedNodes(SyntaxNode root)
            {
                // The initializer can be the access or the call itself, so the root counts as a reached node too.
                // A local function is entered from a call to it, the way a method is, rather than from the body
                // it is declared in, that body not running it unless it calls it
                foreach (SyntaxNode node in root.DescendantNodesAndSelf(descendant => descendant is not LocalFunctionStatementSyntax || ReferenceEquals(descendant, root)))
                {
                    token.ThrowIfCancellationRequested();

                    if (node is not
                        (IdentifierNameSyntax or
                         MemberAccessExpressionSyntax or
                         InvocationExpressionSyntax or
                         BaseObjectCreationExpressionSyntax))
                    {
                        continue;
                    }

                    switch (semanticModel.For(node).GetOperation(node, token))
                    {
                        case IFieldReferenceOperation { Field: { IsStatic: true, IsConst: false } reachedField } reference:
                            if (pending.Contains(reachedField))
                            {
                                // A write to the field being initialized is overwritten by its initializer in
                                // the generated HLSL as well, so it is the one access that is not reported
                                if ((!IsWrite(reference) || !SymbolEqualityComparer.Default.Equals(reachedField, field)) &&
                                    reportedAccesses.Add(node))
                                {
                                    diagnostics.Add(StaticFieldAccessedBeforeInitialization, node, reachedField, field);
                                }
                            }
                            else if (!SymbolEqualityComparer.Default.Equals(reachedField.ContainingType, field.ContainingType) &&
                                     reachedField.TryGetSyntaxNode(token, out VariableDeclaratorSyntax? reachedDeclarator))
                            {
                                WalkReachedDeclaration(reachedField, reachedDeclarator.Initializer?.Value);
                            }

                            break;
                        case IInvocationOperation { TargetMethod: { MethodKind: MethodKind.LocalFunction } localFunction }:
                            WalkReachedDeclaration(localFunction, localFunction.TryGetSyntaxNode(token, out LocalFunctionStatementSyntax? localFunctionStatement) ? localFunctionStatement : null);
                            break;
                        case IInvocationOperation { TargetMethod: { } method }:
                            WalkReachedDeclaration(method, method.TryGetSyntaxNode(token, out MethodDeclarationSyntax? methodDeclaration) ? methodDeclaration : null);
                            break;
                        case IObjectCreationOperation { Constructor: { } constructor }:
                            WalkReachedDeclaration(constructor, constructor.TryGetSyntaxNode(token, out ConstructorDeclarationSyntax? constructorDeclaration) ? constructorDeclaration : null);
                            break;
                    }
                }
            }
        }
    }
}