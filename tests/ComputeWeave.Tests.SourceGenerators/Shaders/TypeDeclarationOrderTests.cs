using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using ComputeWeave.SourceGenerators;
using ComputeWeave.Tests.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests.SourceGenerators.Shaders;

/// <summary>
/// The order the custom types of a shader are declared in, and which of them are forward declared.
/// </summary>
/// <remarks>
/// <para>
/// HLSL needs a custom type declared ahead of every declaration naming it, which is a field of another custom
/// type or a member method prototype one holds. A field needs the type complete, and C# reports a cycle through
/// fields, so an order always exists for them. Prototypes can name each other in a cycle, which no order
/// resolves, and then a forward declaration of the type named ahead of its declaration is the only way through.
/// </para>
/// <para>
/// Every type used to be forward declared, which FXC rejects, and FXC is what an extracted source is compiled
/// with for Direct3D 11. The shaders here carry a thread group size, which turns shader compilation on, so an
/// order DXC rejects reaches these tests as a diagnostic rather than as a passing string match.
/// </para>
/// </remarks>
[TestClass]
public class TypeDeclarationOrderTests
{
    /// <summary>
    /// A prototype naming a type discovered after the one holding it, with no cycle.
    /// </summary>
    private const string PrototypeSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public float x;

            public float Read(Second other) => x + other.y;
        }

        internal struct Second
        {
            public float y;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;
                Second second = default;

                this.buffer[0] = first.Read(second);
            }
        }
        """;

    /// <summary>
    /// A constructor naming a type discovered after the one it constructs.
    /// </summary>
    private const string ConstructorSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public float x;

            public First(Second seed)
            {
                x = seed.y;
            }
        }

        internal struct Second
        {
            public float y;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;
                Second second = default;

                first = new First(second);

                this.buffer[0] = first.x;
            }
        }
        """;

    /// <summary>
    /// A return type naming a type discovered after the one holding the prototype.
    /// </summary>
    private const string ReturnTypeSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public float x;

            public Second Make() => default;
        }

        internal struct Second
        {
            public float y;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;
                Second second = first.Make();

                this.buffer[0] = first.x + second.y;
            }
        }
        """;

    /// <summary>
    /// A type whose members name the type itself, as a parameter and as a return type.
    /// </summary>
    private const string SelfPrototypeSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public float x;

            public float Combine(First other) => x + other.x;

            public First Make() => default;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;
                First other = first.Make();

                this.buffer[0] = first.Combine(other);
            }
        }
        """;

    /// <summary>
    /// Two types whose prototypes name each other, which is the reproduction from the issue this was measured for.
    /// </summary>
    private const string CycleSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public float x;

            public float Combine(Second other) => x + other.y;
        }

        internal struct Second
        {
            public float y;

            public float Combine(First other) => y + other.x;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;
                Second second = default;

                this.buffer[0] = first.Combine(second) + second.Combine(first);
            }
        }
        """;

    /// <summary>
    /// The same cycle, with two members of the first type naming the second one.
    /// </summary>
    private const string CycleNamedTwiceSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public float x;

            public float Combine(Second other) => x + other.y;

            public Second Make() => default;
        }

        internal struct Second
        {
            public float y;

            public float Combine(First other) => y + other.x;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;
                Second second = first.Make();

                this.buffer[0] = first.Combine(second) + second.Combine(first);
            }
        }
        """;

    /// <summary>
    /// A type holding a field of one of the two in a cycle, discovered ahead of both.
    /// </summary>
    private const string FieldBehindCycleSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public float x;

            public float Combine(Second other) => x + other.y;
        }

        internal struct Second
        {
            public float y;

            public float Combine(First other) => y + other.x;
        }

        internal struct Holder
        {
            public Second second;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                Holder holder = default;
                First first = default;

                this.buffer[0] = first.Combine(holder.second) + holder.second.Combine(first);
            }
        }
        """;

    /// <summary>
    /// Two types holding a field of each other, which C# reports as a layout cycle.
    /// </summary>
    private const string FieldCycleSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public Second second;
        }

        internal struct Second
        {
            public First first;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;

                this.buffer[0] = 1;
            }
        }
        """;

    /// <summary>
    /// A type holding a field of its own type, which C# reports as a layout cycle.
    /// </summary>
    private const string SelfFieldSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public First first;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;

                this.buffer[0] = 1;
            }
        }
        """;

    /// <summary>
    /// Two types in a layout cycle whose prototypes name each other as well, which is the one shape the
    /// ordering would forward declare a type of the layout cycle for.
    /// </summary>
    private const string OverlappingCyclesSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public Second second;

            public float A(Second other) => 1;
        }

        internal struct Second
        {
            public First first;

            public float B(First other) => 2;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                First first = default;
                Second second = default;

                this.buffer[0] = first.A(second) + second.B(first);
            }
        }
        """;

    /// <summary>
    /// A type holding a field of one of the two in a layout cycle, discovered ahead of both.
    /// </summary>
    private const string HolderOfFieldCycleSource = """
        using ComputeWeave;

        namespace Shaders;

        internal struct First
        {
            public Second second;
        }

        internal struct Second
        {
            public First first;
        }

        internal struct Holder
        {
            public First first;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private readonly ReadWriteBuffer<float> buffer;

            public void Execute()
            {
                Holder holder = default;

                this.buffer[0] = 1;
            }
        }
        """;

    /// <summary>
    /// The type a prototype names is declared ahead of the one holding the prototype, and nothing is forward
    /// declared, so the source is one FXC accepts as well.
    /// </summary>
    [TestMethod]
    [DataRow(PrototypeSource, "TypeOrderPrototypeTests")]
    [DataRow(ConstructorSource, "TypeOrderConstructorTests")]
    [DataRow(ReturnTypeSource, "TypeOrderReturnTypeTests")]
    public void ATypeAPrototypeNamesIsDeclaredAheadOfIt(string source, string assemblyName)
    {
        string generated = Generate(source, assemblyName);

        CollectionAssert.AreEqual(new[] { "Shaders_Second", "Shaders_First" }, Definitions(generated), generated);
        Assert.AreEqual(0, ForwardDeclarations(generated).Length, generated);
    }

    /// <summary>
    /// A type is declared by the time its own prototypes are read, so a member naming its own type asks for
    /// nothing, and both compilers take the declaration as it stands.
    /// </summary>
    [TestMethod]
    public void ATypeNamedByItsOwnPrototypesIsNotForwardDeclared()
    {
        string generated = Generate(SelfPrototypeSource, "TypeOrderSelfPrototypeTests");

        CollectionAssert.AreEqual(new[] { "Shaders_First" }, Definitions(generated), generated);
        Assert.AreEqual(0, ForwardDeclarations(generated).Length, generated);
    }

    /// <summary>
    /// No order resolves a cycle, so the type named ahead of its declaration is forward declared, and only that
    /// one, once: the first type in line is declared first, and the other is what its prototypes name.
    /// </summary>
    [TestMethod]
    [DataRow(CycleSource, "TypeOrderCycleTests")]
    [DataRow(CycleNamedTwiceSource, "TypeOrderCycleNamedTwiceTests")]
    public void ATypeNamedAcrossACycleIsForwardDeclared(string source, string assemblyName)
    {
        string generated = Generate(source, assemblyName);

        CollectionAssert.AreEqual(new[] { "Shaders_First", "Shaders_Second" }, Definitions(generated), generated);
        CollectionAssert.AreEqual(new[] { "Shaders_Second" }, ForwardDeclarations(generated), generated);
    }

    /// <summary>
    /// A field needs its type complete, which a forward declaration does not give, so a type holding a field
    /// of one in the cycle keeps its turn behind that one even when it is first in line.
    /// </summary>
    [TestMethod]
    public void AFieldKeepsItsTurnBehindItsType()
    {
        string generated = Generate(FieldBehindCycleSource, "TypeOrderFieldBehindCycleTests");

        CollectionAssert.AreEqual(new[] { "Shaders_Second", "Shaders_First", "Shaders_Holder" }, Definitions(generated), generated);
        CollectionAssert.AreEqual(new[] { "Shaders_First" }, ForwardDeclarations(generated), generated);
    }

    /// <summary>
    /// A cycle through fields, which C# reports as a layout cycle and the generator meets all the same, running
    /// over source the compiler has rejected. HLSL cannot lay such a type out either, and the shader compiler
    /// runs out of stack on a use of it rather than reporting it, so every type on the cycle is refused as an
    /// invalid type, none of them is declared, and shader compilation is skipped the way it is for any refusal.
    /// </summary>
    /// <remarks>
    /// The timeout pins the run ending. The ordering used to wait forever for a field of such a type to be
    /// declared, and a generator that spins reports nothing, so no assertion on its output stands in for it.
    /// </remarks>
    [TestMethod]
    [Timeout(120000)]
    [DataRow(FieldCycleSource, "TypeOrderFieldCycleTests", new[] { "Shaders.First", "Shaders.Second" }, new string[0])]
    [DataRow(SelfFieldSource, "TypeOrderSelfFieldTests", new[] { "Shaders.First" }, new string[0])]
    [DataRow(OverlappingCyclesSource, "TypeOrderOverlappingCyclesTests", new[] { "Shaders.First", "Shaders.Second" }, new string[0])]
    [DataRow(HolderOfFieldCycleSource, "TypeOrderHolderOfFieldCycleTests", new[] { "Shaders.First", "Shaders.Second" }, new[] { "Shaders_Holder" })]
    public void ATypeInALayoutCycleIsRefused(string source, string assemblyName, string[] refusedTypes, string[] declaredTypes)
    {
        CSharpCompilation compilation = CompilationHelper.CreateCompilationAllowingErrors(source, assemblyName);

        Assert.IsTrue(
            compilation.GetDiagnostics().Any(static diagnostic => diagnostic.Id == "CS0523"),
            string.Join(", ", compilation.GetDiagnostics().Select(static diagnostic => diagnostic.Id).Distinct()));

        GeneratorDriver driver = GeneratorHelper.CreateDriver(new ComputeShaderDescriptorGenerator());
        GeneratorRunResult result = driver.RunGenerators(compilation).GetRunResult().Results[0];

        Assert.IsNull(result.Exception, result.Exception?.ToString());

        // The invalid type is named by the message, and it is reported once per type on the cycle
        string[] reported = [.. result.Diagnostics.Where(static diagnostic => diagnostic.Id == "CMPW0050").Select(static diagnostic => Regex.Match(diagnostic.GetMessage(), @"uses the invalid type (\S+)").Groups[1].Value)];

        CollectionAssert.AreEquivalent(refusedTypes, reported, string.Join(", ", result.Diagnostics));
        Assert.AreEqual(refusedTypes.Length, result.Diagnostics.Length, string.Join(", ", result.Diagnostics));

        string generated = GeneratorHelper.GetGeneratedSource(result.GeneratedSources, "Shaders.Shader");

        CollectionAssert.AreEqual(declaredTypes, Definitions(generated), generated);
        Assert.AreEqual(0, ForwardDeclarations(generated).Length, generated);
    }

    /// <summary>
    /// The names of the custom types a generated source defines, in the order it defines them.
    /// </summary>
    /// <param name="generated">The generated source.</param>
    /// <returns>The defined type names.</returns>
    private static string[] Definitions(string generated)
    {
        return [.. Regex.Matches(generated, @"^\s*struct (\w+)\s*$", RegexOptions.Multiline).Select(static match => match.Groups[1].Value)];
    }

    /// <summary>
    /// The names of the custom types a generated source forward declares, in the order it declares them.
    /// </summary>
    /// <param name="generated">The generated source.</param>
    /// <returns>The forward declared type names.</returns>
    private static string[] ForwardDeclarations(string generated)
    {
        return [.. Regex.Matches(generated, @"^\s*struct (\w+);\s*$", RegexOptions.Multiline).Select(static match => match.Groups[1].Value)];
    }

    /// <summary>
    /// Runs the generator over a source, asserts it reported nothing, and returns the generated shader source.
    /// </summary>
    /// <param name="source">The source to compile.</param>
    /// <param name="assemblyName">The assembly name to compile under.</param>
    /// <returns>The generated source for the shader.</returns>
    private static string Generate(string source, string assemblyName)
    {
        CSharpCompilation compilation = CompilationHelper.CreateCompilation(
            [source],
            assemblyName,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        GeneratorDriver driver = GeneratorHelper.CreateDriver(new ComputeShaderDescriptorGenerator());
        GeneratorRunResult result = driver.RunGenerators(compilation).GetRunResult().Results[0];

        Assert.IsNull(result.Exception, result.Exception?.ToString());

        ImmutableArray<Diagnostic> diagnostics = result.Diagnostics;

        Assert.IsTrue(
            diagnostics.IsEmpty,
            string.Join(", ", diagnostics.Select(static diagnostic => diagnostic.ToString())));

        return GeneratorHelper.GetGeneratedSource(result.GeneratedSources, "Shaders.Shader");
    }
}
