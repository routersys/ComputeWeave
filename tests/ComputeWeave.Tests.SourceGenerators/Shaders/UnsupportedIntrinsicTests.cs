using System.Collections.Immutable;
using System.Linq;
using ComputeWeave.SourceGenerators;
using ComputeWeave.Tests.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests.SourceGenerators.Shaders;

/// <summary>
/// What an intrinsic of the pixel stage is answered with in a compute shader. The shared Hlsl type declares
/// the nine for the pixel shaders written with the same type, and DXC refuses each of them under cs_6_0.
/// </summary>
/// <remarks>
/// The refusal is the generator's, reported from the rewriting the compute generator alone runs. It was an
/// analyzer before, which read every invocation of the compilation whatever type held it, so a project
/// referencing the Direct2D product as well was refused a pixel shader using the clip or a derivative. The
/// last row here is that property: a type that is no compute shader is left alone.
/// </remarks>
[TestClass]
public class UnsupportedIntrinsicTests
{
    [TestMethod]
    [DataRow("AbortIntrinsicTests", "Hlsl.Abort();", "Hlsl.Abort()")]
    [DataRow("ClipIntrinsicTests", "Hlsl.Clip(this.buffer[0]);", "Hlsl.Clip(this.buffer[0])")]
    [DataRow("DerivativeOfDxIntrinsicTests", "this.buffer[0] = Hlsl.DerivativeOfDx(this.buffer[1]);", "Hlsl.DerivativeOfDx(this.buffer[1])")]
    [DataRow("DerivativeOfDxHighPrecisionIntrinsicTests", "this.buffer[0] = Hlsl.DerivativeOfDxHighPrecision(this.buffer[1]);", "Hlsl.DerivativeOfDxHighPrecision(this.buffer[1])")]
    [DataRow("DerivativeOfDxLowPrecisionIntrinsicTests", "this.buffer[0] = Hlsl.DerivativeOfDxLowPrecision(this.buffer[1]);", "Hlsl.DerivativeOfDxLowPrecision(this.buffer[1])")]
    [DataRow("DerivativeOfDyIntrinsicTests", "this.buffer[0] = Hlsl.DerivativeOfDy(this.buffer[1]);", "Hlsl.DerivativeOfDy(this.buffer[1])")]
    [DataRow("DerivativeOfDyHighPrecisionIntrinsicTests", "this.buffer[0] = Hlsl.DerivativeOfDyHighPrecision(this.buffer[1]);", "Hlsl.DerivativeOfDyHighPrecision(this.buffer[1])")]
    [DataRow("DerivativeOfDyLowPrecisionIntrinsicTests", "this.buffer[0] = Hlsl.DerivativeOfDyLowPrecision(this.buffer[1]);", "Hlsl.DerivativeOfDyLowPrecision(this.buffer[1])")]
    [DataRow("FwidthIntrinsicTests", "this.buffer[0] = Hlsl.Fwidth(this.buffer[1]);", "Hlsl.Fwidth(this.buffer[1])")]
    public void AnIntrinsicOfThePixelStageIsDiagnosedAtTheCall(string assemblyName, string body, string expectedCall)
    {
        AssertReportsOnlyAt(Shader("", "", body), assemblyName, expectedCall);
    }

    /// <summary>
    /// A derivative in a static field initializer, the one route besides a body an intrinsic returning a value
    /// can be written into, which the static field rewriter answers for.
    /// </summary>
    [TestMethod]
    public void AnIntrinsicOfThePixelStageInAStaticFieldInitializerIsDiagnosedAtTheCall()
    {
        AssertReportsOnlyAt(
            Shader("", "private static readonly float Slope = Hlsl.DerivativeOfDx(1.0f);", "this.buffer[0] = Slope;"),
            "DerivativeInInitializerTests",
            "Hlsl.DerivativeOfDx(1.0f)");
    }

    /// <summary>
    /// The message names the intrinsic and the reason, so the author reads why rather than only that.
    /// </summary>
    [TestMethod]
    public void TheMessageNamesTheIntrinsicAndTheReason()
    {
        Diagnostic diagnostic = Run(Shader("", "", "Hlsl.Clip(this.buffer[0]);"), "ClipMessageTests").Diagnostics.Single();
        string message = diagnostic.GetMessage();

        Assert.IsTrue(message.Contains("Clip"), message);
        Assert.IsTrue(message.Contains("no pixel to discard"), message);
    }

    /// <summary>
    /// Intrinsics a compute shader can use, left alone and handed to the compiler, so that the rows above answer
    /// for the stage and not for the intrinsic being one of the Hlsl type.
    /// </summary>
    [TestMethod]
    public void AnIntrinsicAComputeShaderCanUseIsNotDiagnosed()
    {
        GeneratorRunResult result = Run(
            Shader("", "", "this.buffer[0] = Hlsl.Sqrt(this.buffer[1]) + Hlsl.SmoothStep(0, 1, this.buffer[2]);"),
            "SupportedIntrinsicTests");

        Assert.IsTrue(result.Diagnostics.IsEmpty, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.Id)));
        Assert.AreNotEqual(0, GeneratorHelper.GetGeneratedSource(result.GeneratedSources, "Shaders.Shader").Length);
    }

    /// <summary>
    /// A method of another type carrying the name of a refused intrinsic, which is a method with source the shader
    /// imports and not an intrinsic, so the name alone decides nothing.
    /// </summary>
    [TestMethod]
    public void AMethodOfTheSameNameOnAnotherTypeIsNotDiagnosed()
    {
        const string declarations = """
            internal static class Stage
            {
                public static float Clip(float value) => value;
            }
            """;

        GeneratorRunResult result = Run(Shader(declarations, "", "this.buffer[0] = Stage.Clip(this.buffer[1]);"), "SameNameOnAnotherTypeTests");

        Assert.IsTrue(result.Diagnostics.IsEmpty, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.Id)));
        Assert.AreNotEqual(0, GeneratorHelper.GetGeneratedSource(result.GeneratedSources, "Shaders.Shader").Length);
    }

    /// <summary>
    /// A type that is no compute shader, using the intrinsics of the pixel stage the way a Direct2D pixel shader
    /// does in a project referencing both products. The generator has no shader to write it into, so it is left
    /// alone, which the analyzer this replaces did not do.
    /// </summary>
    [TestMethod]
    public void AnIntrinsicOfThePixelStageOutsideAComputeShaderIsLeftAlone()
    {
        const string declarations = """
            public interface IPixelShaderLike
            {
                float Execute();
            }

            internal readonly partial struct Effect : IPixelShaderLike
            {
                private readonly float time;

                public float Execute()
                {
                    Hlsl.Clip(this.time);

                    return Hlsl.DerivativeOfDx(this.time);
                }
            }
            """;

        GeneratorRunResult result = Run(Shader(declarations, "", "this.buffer[0] = 1.0f;"), "PixelStageIntrinsicOutsideAShaderTests");

        Assert.IsTrue(result.Diagnostics.IsEmpty, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.Id)));
        Assert.AreNotEqual(0, GeneratorHelper.GetGeneratedSource(result.GeneratedSources, "Shaders.Shader").Length);
    }

    private static string Shader(string declarations, string members, string body)
    {
        return $$"""
            using ComputeWeave;

            namespace Shaders;

            {{declarations}}

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                {{members}}

                private readonly ReadWriteBuffer<float> buffer;

                public void Execute()
                {
                    {{body}}
                }
            }
            """;
    }

    private static void AssertReportsOnlyAt(string source, string assemblyName, string expectedCall)
    {
        ImmutableArray<Diagnostic> diagnostics = Run(source, assemblyName).Diagnostics;

        // Not made distinct, so that one cause reported twice fails rather than reading as one report
        string[] actualIds = [.. diagnostics.Select(static diagnostic => diagnostic.Id).Order()];

        Assert.IsTrue(actualIds.SequenceEqual(["CMPW0112"]), $"CMPW0112 is not the only report: {string.Join(", ", actualIds)}");

        // The location is read back through the tree, so a report bound to none fails here rather than reading as a match
        Diagnostic diagnostic = diagnostics[0];

        Assert.AreEqual(expectedCall, diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    private static GeneratorRunResult Run(string source, string assemblyName)
    {
        CSharpCompilation compilation = CompilationHelper.CreateCompilation(
            [source],
            assemblyName,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        GeneratorDriver driver = GeneratorHelper.CreateDriver(new ComputeShaderDescriptorGenerator());
        GeneratorRunResult result = driver.RunGenerators(compilation).GetRunResult().Results[0];

        // A fault discards the output for the whole compilation unit, so it is read ahead of the reports
        Assert.IsNull(result.Exception, result.Exception?.ToString());

        return result;
    }
}
