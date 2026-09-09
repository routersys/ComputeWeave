using System.Collections.Immutable;
using System.Linq;
using ComputeWeave.SourceGeneration.Extensions;
using ComputeWeave.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using static ComputeWeave.SourceGeneration.Diagnostics.DiagnosticDescriptors;

namespace ComputeWeave.SourceGenerators;

/// <summary>
/// A diagnostic analyzer that generates diagnostics for invalid uses of [ThreadGroupSize].
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidThreadGroupSizeAttributeUseAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        MissingThreadGroupSizeAttribute,
        InvalidThreadGroupSizeAttributeDefaultThreadGroupSizes,
        InvalidThreadGroupSizeAttributeValues,
        InvalidThreadGroupSizeAttributeDepthOnPixelShaderLikeType,
    ];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static context =>
        {
            // Get the IComputeShader, IComputeShader<TPixel> and [ThreadGroupSize] symbols
            if (context.Compilation.GetTypeByMetadataName("ComputeWeave.IComputeShader") is not { } computeShaderSymbol ||
                context.Compilation.GetTypeByMetadataName("ComputeWeave.IComputeShader`1") is not { } pixelShaderSymbol ||
                context.Compilation.GetTypeByMetadataName("ComputeWeave.ThreadGroupSizeAttribute") is not { } threadGroupSizeAttributeSymbol)
            {
                return;
            }

            context.RegisterSymbolAction(context =>
            {
                // Only struct types are possible targets
                if (context.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct } typeSymbol)
                {
                    return;
                }

                // If the type is not a compute shader type, immediately bail out
                if (!MissingComputeShaderDescriptorOnComputeShaderAnalyzer.IsComputeShaderType(typeSymbol, computeShaderSymbol, pixelShaderSymbol))
                {
                    return;
                }

                // Warn if the shader type is not using [ThreadGroupSize]
                if (!typeSymbol.TryGetAttributeWithType(threadGroupSizeAttributeSymbol, out AttributeData? attributeData))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MissingThreadGroupSizeAttribute,
                        typeSymbol.Locations.First(),
                        typeSymbol));

                    return;
                }

                // If there is a single argument, validate that it is valid
                if (attributeData.ConstructorArguments is [{ Value: var defaultSize }])
                {
                    int? rawDefaultSize = defaultSize as int?;

                    // The named values carry sizes of their own, so the depth is read from the value they stand for
                    if (!DefaultThreadGroupSizeLookup.TryGetSizes((DefaultThreadGroupSizes?)rawDefaultSize, out _, out _, out int defaultThreadsZ))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            InvalidThreadGroupSizeAttributeDefaultThreadGroupSizes,
                            attributeData.GetLocation(),
                            typeSymbol));

                        return;
                    }

                    ReportIfDeeperThanOnePixelShaderLikeType(context, typeSymbol, attributeData, defaultThreadsZ, pixelShaderSymbol);

                    return;
                }

                // If there are three arguments, validate that they are also valid thread group sizes
                // The axes are bounded on their own, and a thread group is also bounded as a whole
                if (attributeData.ConstructorArguments is not [{ Value: int threadsX }, { Value: int threadsY }, { Value: int threadsZ }] ||
                    threadsX is < 1 or > 1024 ||
                    threadsY is < 1 or > 1024 ||
                    threadsZ is < 1 or > 64 ||
                    threadsX * threadsY * threadsZ > 1024)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        InvalidThreadGroupSizeAttributeValues,
                        attributeData.GetLocation(),
                        typeSymbol));

                    return;
                }

                ReportIfDeeperThanOnePixelShaderLikeType(context, typeSymbol, attributeData, threadsZ, pixelShaderSymbol);
            }, SymbolKind.NamedType);
        });
    }

    /// <summary>
    /// Reports a diagnostic if a "pixel shader like" type declares a thread group deeper than one on the Z axis.
    /// </summary>
    /// <param name="context">The <see cref="SymbolAnalysisContext"/> to report into.</param>
    /// <param name="typeSymbol">The shader type being analyzed.</param>
    /// <param name="attributeData">The <c>[ThreadGroupSize]</c> attribute the sizes were read from.</param>
    /// <param name="threadsZ">The number of threads the group holds on the Z axis.</param>
    /// <param name="pixelShaderSymbol">The type symbol for <c>IComputeShader&lt;TPixel&gt;</c>.</param>
    /// <remarks>
    /// The dispatch for these shaders fixes the Z extent at one, and the generated entry point compares the X
    /// and Y axes only, so the threads the group holds on the Z axis all reach the body for the same pixel.
    /// </remarks>
    private static void ReportIfDeeperThanOnePixelShaderLikeType(
        SymbolAnalysisContext context,
        INamedTypeSymbol typeSymbol,
        AttributeData attributeData,
        int threadsZ,
        INamedTypeSymbol pixelShaderSymbol)
    {
        if (threadsZ > 1 &&
            MissingComputeShaderDescriptorOnComputeShaderAnalyzer.IsPixelShaderLikeType(typeSymbol, pixelShaderSymbol))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidThreadGroupSizeAttributeDepthOnPixelShaderLikeType,
                attributeData.GetLocation(),
                typeSymbol,
                threadsZ));
        }
    }
}