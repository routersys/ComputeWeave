using System.Collections.Immutable;
using System.Linq;
using ComputeWeave.SourceGenerators;
using ComputeWeave.Tests.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests.SourceGenerators.Shaders;

/// <summary>
/// A call to a method of the shader type, written through a qualifier. The generator writes those methods
/// out at the top level under their own names, so the qualifier names nothing in the generated HLSL, and a
/// call written through one has to be written the way the same call written by name alone is.
/// </summary>
/// <remarks>
/// The shaders here carry a thread group size, which is what turns shader compilation on, so a qualifier
/// that reaches the generated HLSL arrives as a diagnostic rather than as a string the assertions could miss.
/// </remarks>
[TestClass]
public class QualifiedShaderMethodCallTests
{
    /// <summary>
    /// A static method called through the shader type, under each spelling the type can be named by. The
    /// alias is declared for the row that calls through it.
    /// </summary>
    [TestMethod]
    [DataRow("Shader.Twice()", "QualifiedCallTypeNameTests")]
    [DataRow("Shaders.Shader.Twice()", "QualifiedCallNamespaceTests")]
    [DataRow("global::Shaders.Shader.Twice()", "QualifiedCallGlobalTests")]
    [DataRow("S.Twice()", "QualifiedCallAliasTests")]
    public void AStaticMethodCalledThroughTheShaderTypeIsNamedDirectly(string call, string assemblyName)
    {
        string source = $$"""
            using ComputeWeave;
            using S = Shaders.Shader;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                private static float Twice() => 2.0f;

                public void Execute()
                {
                    this.buffer[0] = {{call}};
                }
            }
            """;

        AssertNamedDirectly(source, assemblyName, "= Twice()");
    }

    /// <summary>
    /// The same call in a static field initializer, which a rewriter of its own handles.
    /// </summary>
    [TestMethod]
    public void AStaticMethodCalledThroughTheShaderTypeInAnInitializerIsNamedDirectly()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private static readonly float Scale = Shader.Twice();

                private readonly ReadWriteBuffer<float> buffer;

                private static float Twice() => 2.0f;

                public void Execute()
                {
                    this.buffer[0] = Scale;
                }
            }
            """;

        AssertNamedDirectly(Source, "QualifiedCallInitializerTests", "= Twice()");
    }

    /// <summary>
    /// The same call from a method declared outside the shader, where the type name is the only way to
    /// write it. The declaration is imported by a rewriter created for it.
    /// </summary>
    [TestMethod]
    public void AStaticMethodCalledThroughTheShaderTypeFromAnImportedMethodIsNamedDirectly()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            internal static class Helper
            {
                public static float Go() => Shader.Twice();
            }

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                internal static float Twice() => 2.0f;

                public void Execute()
                {
                    this.buffer[0] = Helper.Go();
                }
            }
            """;

        AssertNamedDirectly(Source, "QualifiedCallImportedMethodTests", "return Twice()");
    }

    /// <summary>
    /// An instance method called through <see langword="this"/>. The generator writes it out the way it
    /// writes a static one, so the qualifier names nothing there either.
    /// </summary>
    [TestMethod]
    public void AnInstanceMethodCalledThroughThisIsNamedDirectly()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                private float Twice() => 2.0f;

                public void Execute()
                {
                    this.buffer[0] = this.Twice();
                }
            }
            """;

        AssertNamedDirectly(Source, "QualifiedCallThisTests", "= Twice()");
    }

    /// <summary>
    /// A method named after an HLSL keyword, called through the shader type. Its declaration is written
    /// under the mapped name, so the call has to keep the mapping rather than the name the author wrote.
    /// </summary>
    [TestMethod]
    public void AMethodNamedAfterAKeywordCalledThroughTheShaderTypeKeepsItsMappedName()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                private static float sample() => 2.0f;

                public void Execute()
                {
                    this.buffer[0] = Shader.sample();
                }
            }
            """;

        AssertNamedDirectly(Source, "QualifiedCallKeywordTests", "= __reserved__sample()");
    }

    /// <summary>
    /// Asserts that the generated HLSL compiles and calls a method of the shader by its name alone.
    /// </summary>
    /// <param name="source">The shader source to generate from.</param>
    /// <param name="assemblyName">The assembly name to compile under.</param>
    /// <param name="written">The call as it has to be written, with what precedes it on the line.</param>
    private static void AssertNamedDirectly(string source, string assemblyName, string written)
    {
        CSharpCompilation compilation = CompilationHelper.CreateCompilation(
            [source],
            assemblyName,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        GeneratorDriver driver = GeneratorHelper.CreateDriver(new ComputeShaderDescriptorGenerator());
        GeneratorRunResult result = driver.RunGenerators(compilation).GetRunResult().Results[0];

        Assert.IsNull(result.Exception, result.Exception?.ToString());

        ImmutableArray<Diagnostic> diagnostics = result.Diagnostics;

        // A qualifier left in the generated HLSL is what the shader compiler reports, so this is read first
        Assert.IsTrue(
            diagnostics.IsEmpty,
            string.Join(", ", diagnostics.Select(static diagnostic => diagnostic.ToString())));

        string generated = GeneratorHelper.GetGeneratedSource(result.GeneratedSources, "Shaders.Shader");
        string name = written[(written.LastIndexOf(' ') + 1)..];

        Assert.IsTrue(generated.Contains(written), $"the call is not written by name alone:\n{generated}");
        Assert.IsFalse(generated.Contains($".{name}"), $"the qualifier is written out:\n{generated}");
    }
}
