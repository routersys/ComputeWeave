using System.Linq;
using ComputeWeave.SourceGenerators;
using ComputeWeave.Tests.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests.SourceGenerators.Shaders;

/// <summary>
/// Which literal and constant types the rewriting refuses. A 64 bit integer has no HLSL literal the generator
/// maps, so a literal or a constant of that type used to reach the shader compiler as its digits, which are read
/// as a 32 bit value: a negative literal on the left of a comparison with an unsigned value was compared as
/// unsigned, and a value past the 32 bit range was truncated, with no diagnostic either way.
/// </summary>
/// <remarks>
/// <para>
/// Both are refused by tracking the type, the way a local of that type already is, so the report is the one for
/// an invalid discovered type. A constant is reached by its name alone, through a member access in the body, and
/// through a member access in a static field initializer, which are three rewriting sites, so each has a row.
/// A native integer constant is boxed as a 32 bit value while C# computes with it at the width of the platform,
/// so it is told by its type rather than by its value: it has a row of each sign.
/// </para>
/// <para>
/// The last rows are the controls. A constant of a type the set does have is still written as a definition, a
/// decimal constant among them, since its floating point spelling is the deliberate mapping.
/// </para>
/// </remarks>
[TestClass]
public class ConstantAndLiteralTypeTests
{
    /// <summary>
    /// The shader the rows are written over. The values are read from the constant buffer, so nothing is folded
    /// before the rewriting sees it.
    /// </summary>
    private const string Template = """
        using ComputeWeave;

        namespace Shaders;

        internal static class Helper
        {
            public const long Wide = 5;

            public const uint Narrow = 5;

            public const nuint UnsignedNative = 5;
        }

        [ThreadGroupSize(DefaultThreadGroupSizes.X)]
        [GeneratedComputeShaderDescriptor]
        internal readonly partial struct Shader : IComputeShader
        {
            private const long Big = 5;

            private const nint Native = 5;

            private const int Small = 5;

            private const decimal Half = 0.5m;

            private const string Name = "x";

            private const string Other = "y";

            private static readonly float Seeded = {{INITIALIZER}};

            private readonly ReadWriteBuffer<float> buffer;

            private readonly int signed;

            private readonly uint unsigned;

            public void Execute()
            {
                {{BODY}}
            }
        }
        """;

    [TestMethod]
    public void ALongLiteralIsRefused()
    {
        AssertReports(Shader("this.buffer[0] = 5L < this.unsigned ? 1.0f : 0.0f;"), "LongLiteralTests", "CMPW0050");
    }

    [TestMethod]
    public void AnUnsignedLongLiteralIsRefused()
    {
        AssertReports(Shader("this.buffer[0] = (float)(5UL + this.unsigned);"), "UnsignedLongLiteralTests", "CMPW0050");
    }

    [TestMethod]
    public void ALongConstantReadByNameIsRefused()
    {
        AssertReports(Shader("this.buffer[0] = (float)(Big + this.signed);"), "LongConstantByNameTests", "CMPW0050");
    }

    [TestMethod]
    public void ALongConstantReadThroughAMemberAccessIsRefused()
    {
        AssertReports(Shader("this.buffer[0] = (float)(Helper.Wide + this.signed);"), "LongConstantMemberAccessTests", "CMPW0050");
    }

    [TestMethod]
    public void ALongConstantInAStaticFieldInitializerIsRefused()
    {
        AssertReports(Shader("this.buffer[0] = Seeded;", "Helper.Wide + 1"), "LongConstantInitializerTests", "CMPW0050");
    }

    [TestMethod]
    public void ANativeIntegerConstantIsRefused()
    {
        AssertReports(Shader("this.buffer[0] = (float)(Native + this.signed);"), "NativeIntegerConstantTests", "CMPW0050");
    }

    [TestMethod]
    public void AnUnsignedNativeIntegerConstantReadThroughAMemberAccessIsRefused()
    {
        AssertReports(Shader("this.buffer[0] = (float)(Helper.UnsignedNative + this.unsigned);"), "UnsignedNativeIntegerConstantTests", "CMPW0050");
    }

    /// <summary>
    /// A string constant has no HLSL form either, and used to be left as an identifier the shader compiler could
    /// not resolve. It goes through the same refusal now. The comparison is between two constants so that no
    /// string literal adds a report of its own.
    /// </summary>
    [TestMethod]
    public void AStringConstantIsRefused()
    {
        AssertReports(Shader("this.buffer[0] = Name == Other ? 1.0f : 0.0f;"), "StringConstantTests", "CMPW0050");
    }

    [TestMethod]
    public void AnIntegerConstantReadByNameIsAccepted()
    {
        AssertNoDiagnostics(Shader("this.buffer[0] = (float)(Small + this.signed);"), "IntegerConstantByNameTests");
    }

    [TestMethod]
    public void AnUnsignedConstantReadThroughAMemberAccessIsAccepted()
    {
        AssertNoDiagnostics(Shader("this.buffer[0] = (float)(Helper.Narrow + this.unsigned);"), "UnsignedConstantMemberAccessTests");
    }

    [TestMethod]
    public void ADecimalConstantIsAccepted()
    {
        AssertNoDiagnostics(Shader("this.buffer[0] = (float)Half;"), "DecimalConstantTests");
    }

    [TestMethod]
    public void AnIntegerLiteralIsAccepted()
    {
        AssertNoDiagnostics(Shader("this.buffer[0] = (float)(5 + this.signed);"), "IntegerLiteralTests");
    }

    private static string Shader(string body, string initializer = "1.0f")
    {
        return Template.Replace("{{INITIALIZER}}", initializer).Replace("{{BODY}}", body);
    }

    private static void AssertReports(string source, string assemblyName, string expectedId)
    {
        string[] actualIds = Run(source, assemblyName);

        Assert.IsTrue(actualIds.Contains(expectedId), $"{expectedId} is not reported: {string.Join(", ", actualIds)}");
    }

    private static void AssertNoDiagnostics(string source, string assemblyName)
    {
        string[] actualIds = Run(source, assemblyName);

        Assert.AreEqual(0, actualIds.Length, string.Join(", ", actualIds));
    }

    private static string[] Run(string source, string assemblyName)
    {
        CSharpCompilation compilation = CompilationHelper.CreateCompilation(
            [source],
            assemblyName,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        GeneratorDriver driver = GeneratorHelper.CreateDriver(new ComputeShaderDescriptorGenerator());
        GeneratorRunResult result = driver.RunGenerators(compilation).GetRunResult().Results[0];

        Assert.IsNull(result.Exception, result.Exception?.ToString());

        return [.. result.Diagnostics.Select(static diagnostic => diagnostic.Id).Distinct().Order()];
    }
}
