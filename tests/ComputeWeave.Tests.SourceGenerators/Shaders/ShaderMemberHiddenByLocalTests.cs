using System.Collections.Immutable;
using System.Linq;
using ComputeWeave.SourceGenerators;
using ComputeWeave.Tests.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests.SourceGenerators.Shaders;

/// <summary>
/// A member of the shader accessed through a qualifier where a local or a parameter of the same name is in
/// scope. The qualifier is dropped when the access is written out, so HLSL, which resolves a name by scope
/// alone, reads the local where C# read the member.
/// </summary>
/// <remarks>
/// The shaders here carry a thread group size, which is what turns shader compilation on. A shape that is not
/// refused therefore has to compile as well, so a check that refused too little would arrive as a diagnostic
/// from the shader compiler rather than as a passing string match.
/// </remarks>
[TestClass]
public class ShaderMemberHiddenByLocalTests
{
    /// <summary>
    /// A captured field read through <see langword="this"/>, with a local of its name declared ahead of it.
    /// </summary>
    [TestMethod]
    public void ALocalHidingACapturedFieldIsReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;
                private readonly float amount;

                public void Execute()
                {
                    float amount = 3.0f;

                    this.buffer[0] = this.amount + amount;
                }
            }
            """;

        AssertReportedAt(Source, "HiddenCapturedFieldTests", "this.amount");
    }

    /// <summary>
    /// A static field read through the shader type, with a local of its name declared ahead of it.
    /// </summary>
    [TestMethod]
    public void ALocalHidingAStaticFieldIsReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private static readonly float Factor = 2.0f;

                private readonly ReadWriteBuffer<float> buffer;

                public void Execute()
                {
                    float Factor = 3.0f;

                    this.buffer[0] = Shader.Factor + Factor;
                }
            }
            """;

        AssertReportedAt(Source, "HiddenStaticFieldTests", "Shader.Factor");
    }

    /// <summary>
    /// A captured field read through <see langword="this"/> in a method whose parameter has its name.
    /// </summary>
    [TestMethod]
    public void AParameterHidingACapturedFieldIsReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;
                private readonly float amount;

                private float Add(float amount) => this.amount + amount;

                public void Execute()
                {
                    this.buffer[0] = Add(1.0f);
                }
            }
            """;

        AssertReportedAt(Source, "HiddenByParameterTests", "this.amount");
    }

    /// <summary>
    /// A local initialized from the field it is named after. The local is in scope in its own initializer,
    /// and the generated HLSL would read it there before anything assigned it.
    /// </summary>
    [TestMethod]
    public void ALocalInitializedFromTheFieldItHidesIsReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;
                private readonly float amount;

                public void Execute()
                {
                    float amount = this.amount;

                    this.buffer[0] = amount;
                }
            }
            """;

        AssertReportedAt(Source, "HiddenByOwnInitializerTests", "this.amount");
    }

    /// <summary>
    /// A local declared in an argument after the access. The rewriting hoists that declaration ahead of the
    /// body, so it hides the field wherever it was written, unlike a local declared in a statement.
    /// </summary>
    [TestMethod]
    public void AHoistedLocalDeclaredAfterTheAccessIsReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;
                private readonly float amount;

                public void Execute()
                {
                    this.buffer[0] = this.amount;
                    this.buffer[1] = Hlsl.Modf(2.5f, out float amount) + amount;
                }
            }
            """;

        AssertReportedAt(Source, "HiddenByHoistedLocalTests", "this.amount");
    }

    /// <summary>
    /// A local declared in an argument inside a block the access is outside of. C# does not see it at the
    /// access, but the rewriting hoists it ahead of the whole body, where it hides the field for every read.
    /// </summary>
    [TestMethod]
    public void AHoistedLocalOfAnotherBlockIsReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;
                private readonly float amount;

                public void Execute()
                {
                    if (this.buffer[1] > 0.0f)
                    {
                        this.buffer[1] = Hlsl.Modf(2.5f, out float amount) + amount;
                    }

                    this.buffer[0] = this.amount;
                }
            }
            """;

        AssertReportedAt(Source, "HiddenByHoistedLocalOfAnotherBlockTests", "this.amount");
    }

    /// <summary>
    /// A local declared after the access, in a statement. HLSL scopes it from its declaration, the way C#
    /// binds a simple name, so the access ahead of it reads the field and nothing is refused.
    /// </summary>
    [TestMethod]
    public void ALocalDeclaredAfterTheAccessIsNotReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;
                private readonly float amount;

                public void Execute()
                {
                    this.buffer[0] = this.amount;

                    float amount = 3.0f;

                    this.buffer[1] = amount;
                }
            }
            """;

        AssertNotReported(Source, "LaterLocalTests");
    }

    /// <summary>
    /// A local of the enclosing method, with the access inside a static local function. The function is
    /// written out as a function of its own, so the local is not in scope there.
    /// </summary>
    [TestMethod]
    public void ALocalOfTheEnclosingMethodIsNotReportedInsideALocalFunction()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private static readonly float Factor = 2.0f;

                private readonly ReadWriteBuffer<float> buffer;

                public void Execute()
                {
                    float Factor = 3.0f;

                    static float Read() => Shader.Factor;

                    this.buffer[0] = Read() + Factor;
                }
            }
            """;

        AssertNotReported(Source, "EnclosingLocalTests");
    }

    /// <summary>
    /// A local declared in a block the access is outside of. It is out of scope at the access in C# and in
    /// HLSL alike.
    /// </summary>
    [TestMethod]
    public void ALocalOfAnotherBlockIsNotReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;
                private readonly float amount;

                public void Execute()
                {
                    if (this.buffer[1] > 0.0f)
                    {
                        float amount = 3.0f;

                        this.buffer[1] = amount;
                    }

                    this.buffer[0] = this.amount;
                }
            }
            """;

        AssertNotReported(Source, "SiblingBlockLocalTests");
    }

    /// <summary>
    /// A constant read through the shader type beside a local of its name. A constant is written out under
    /// a name of its own, so the local hides nothing and nothing is refused.
    /// </summary>
    [TestMethod]
    public void ALocalNamedAfterAConstantIsNotReported()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private const float Scale = 2.0f;

                private readonly ReadWriteBuffer<float> buffer;

                public void Execute()
                {
                    float Scale = 3.0f;

                    this.buffer[0] = Shader.Scale + Scale;
                }
            }
            """;

        AssertNotReported(Source, "ConstantNamesakeTests");
    }

    /// <summary>
    /// Asserts that one access is refused, at the access as the author wrote it and with nothing else reported.
    /// </summary>
    /// <param name="source">The shader source to generate from.</param>
    /// <param name="assemblyName">The assembly name to compile under.</param>
    /// <param name="access">The text of the access the report has to land on.</param>
    private static void AssertReportedAt(string source, string assemblyName, string access)
    {
        ImmutableArray<Diagnostic> diagnostics = Run(source, assemblyName).Diagnostics;

        Assert.AreEqual(
            "CMPW0131",
            string.Join(", ", diagnostics.Select(static diagnostic => diagnostic.Id)));

        // The report has to name the access the author wrote, which is the place to change
        Assert.AreEqual(access, diagnostics[0].Location.SourceTree!.GetText().ToString(diagnostics[0].Location.SourceSpan));
    }

    /// <summary>
    /// Asserts that nothing is refused and the generated HLSL compiles.
    /// </summary>
    /// <param name="source">The shader source to generate from.</param>
    /// <param name="assemblyName">The assembly name to compile under.</param>
    private static void AssertNotReported(string source, string assemblyName)
    {
        ImmutableArray<Diagnostic> diagnostics = Run(source, assemblyName).Diagnostics;

        Assert.IsTrue(
            diagnostics.IsEmpty,
            string.Join(", ", diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    /// <summary>
    /// Runs the generator over a source and returns the whole run result.
    /// </summary>
    /// <param name="source">The source to compile.</param>
    /// <param name="assemblyName">The assembly name to compile under.</param>
    /// <returns>The result of the run.</returns>
    private static GeneratorRunResult Run(string source, string assemblyName)
    {
        CSharpCompilation compilation = CompilationHelper.CreateCompilation(
            [source],
            assemblyName,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        GeneratorDriver driver = GeneratorHelper.CreateDriver(new ComputeShaderDescriptorGenerator());
        GeneratorRunResult result = driver.RunGenerators(compilation).GetRunResult().Results[0];

        Assert.IsNull(result.Exception, result.Exception?.ToString());

        return result;
    }
}
