using System.Collections.Immutable;
using System.Linq;
using ComputeWeave.SourceGenerators;
using ComputeWeave.Tests.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests.SourceGenerators.Shaders;

/// <summary>
/// What a call leading back to the declaration it is written in is answered with. HLSL has no recursion, so
/// the shader compiler refuses such a function, and before this it did so at the shader type, naming a
/// function of the generated HLSL, which for a local function or a method of a custom type is a name the
/// generator gave it.
/// </summary>
/// <remarks>
/// The rows are the declarations the generated HLSL holds and the ways a cycle can close through them, the
/// calls being recorded from whichever rewriting writes them out: a method of the shader, a method or a
/// constructor of another type, and a local function, whether or not it is called. Each row names the calls
/// the report has to land on, as the author wrote them, so a report at the declaration or at the shader type
/// fails the row as much as a missing one does.
/// </remarks>
[TestClass]
public class RecursiveCallTests
{
    [TestMethod]
    [DataRow(
        "RecursiveStaticMethodOfTheShaderTests",
        "",
        "private static float Twice(float value) => Twice(value) * 2;",
        "this.buffer[0] = Twice(2.0f);",
        new[] { "Twice(value)" })]
    [DataRow(
        "RecursiveInstanceMethodOfTheShaderTests",
        "",
        "private float Twice(float value) => Twice(value) * 2;",
        "this.buffer[0] = Twice(2.0f);",
        new[] { "Twice(value)" })]
    [DataRow(
        "MutuallyRecursiveMethodsOfTheShaderTests",
        "",
        """
        private static float Even(float value) => Odd(value);

            private static float Odd(float value) => Even(value);
        """,
        "this.buffer[0] = Even(2.0f);",
        new[] { "Even(value)", "Odd(value)" })]
    [DataRow(
        "RecursionThroughThreeMethodsOfTheShaderTests",
        "",
        """
        private static float First(float value) => Second(value);

            private static float Second(float value) => Third(value);

            private static float Third(float value) => First(value);
        """,
        "this.buffer[0] = First(2.0f);",
        new[] { "First(value)", "Second(value)", "Third(value)" })]
    [DataRow(
        "RecursiveExternalStaticMethodTests",
        """
        internal static class Helper
        {
            public static float Twice(float value) => Twice(value) * 2;
        }
        """,
        "",
        "this.buffer[0] = Helper.Twice(2.0f);",
        new[] { "Twice(value)" })]
    [DataRow(
        "RecursiveExtensionMethodTests",
        """
        internal static class Helper
        {
            public static float Twice(this float value) => value.Twice() * 2;
        }
        """,
        "",
        "this.buffer[0] = 2.0f.Twice();",
        new[] { "value.Twice()" })]
    [DataRow(
        "RecursionThroughAnExternalStaticMethodTests",
        """
        internal static class Helper
        {
            public static float Back(float value) => Shader.Twice(value);
        }
        """,
        "internal static float Twice(float value) => Helper.Back(value);",
        "this.buffer[0] = Twice(2.0f);",
        new[] { "Helper.Back(value)", "Shader.Twice(value)" })]
    [DataRow(
        "RecursiveInstanceMethodOfACustomTypeTests",
        """
        internal struct Helper
        {
            public float Amount;

            public float Doubled() => Doubled() * 2;
        }
        """,
        "",
        """
        Helper helper = default;

                    this.buffer[0] = helper.Doubled();
        """,
        new[] { "Doubled()" })]
    [DataRow(
        "RecursiveConstructorTests",
        """
        internal struct Helper
        {
            public float Amount;

            public Helper(float amount)
            {
                Amount = new Helper(amount - 1).Amount;
            }
        }
        """,
        "",
        "this.buffer[0] = new Helper(2.0f).Amount;",
        new[] { "new Helper(amount - 1)" })]
    [DataRow(
        "RecursionThroughAConstructorTests",
        """
        internal struct Helper
        {
            public float Amount;

            public Helper(float amount)
            {
                Amount = Make(amount);
            }

            public static float Make(float amount) => new Helper(amount).Amount;
        }
        """,
        "",
        "this.buffer[0] = Helper.Make(2.0f);",
        new[] { "Make(amount)", "new Helper(amount)" })]
    [DataRow(
        "RecursiveLocalFunctionTests",
        "",
        "",
        """
        static float Twice(float value) => Twice(value) * 2;

                    this.buffer[0] = Twice(2.0f);
        """,
        new[] { "Twice(value)" })]
    [DataRow(
        "MutuallyRecursiveLocalFunctionsTests",
        "",
        "",
        """
        static float Even(float value) => Odd(value);

                    static float Odd(float value) => Even(value);

                    this.buffer[0] = Even(2.0f);
        """,
        new[] { "Even(value)", "Odd(value)" })]
    [DataRow(
        "RecursionThroughALocalFunctionTests",
        "",
        """
        private static float Twice(float value)
            {
                static float Back(float inner) => Twice(inner);

                return Back(value) * 2;
            }
        """,
        "this.buffer[0] = Twice(2.0f);",
        new[] { "Back(value)", "Twice(inner)" })]
    [DataRow(
        "RecursiveLocalFunctionInAnImportTests",
        """
        internal static class Helper
        {
            public static float Outer(float value)
            {
                static float Twice(float inner) => Twice(inner) * 2;

                return Twice(value);
            }
        }
        """,
        "",
        "this.buffer[0] = Helper.Outer(2.0f);",
        new[] { "Twice(inner)" })]
    [DataRow(
        "RecursiveMethodReachedFromAStaticFieldInitializerTests",
        """
        internal static class Helper
        {
            public static float Twice(float value) => Twice(value) * 2;
        }
        """,
        "private static readonly float Scale = Helper.Twice(2.0f);",
        "this.buffer[0] = Scale;",
        new[] { "Twice(value)" })]
    [DataRow(
        "RecursivePartialMethodOfTheShaderTests",
        "",
        """
        private static partial float Twice(float value);

            private static partial float Twice(float value) => Twice(value) * 2;
        """,
        "this.buffer[0] = Twice(2.0f);",
        new[] { "Twice(value)" })]
    [DataRow(
        "UncalledRecursiveMethodOfTheShaderTests",
        "",
        "private static float Unused(float value) => Unused(value);",
        "this.buffer[0] = 1.0f;",
        new[] { "Unused(value)" })]
    [DataRow(
        "UncalledRecursiveLocalFunctionTests",
        "",
        "",
        """
        static float Unused(float value) => Unused(value);

                    this.buffer[0] = 1.0f;
        """,
        new[] { "Unused(value)" })]
    public void ARecursiveCallIsDiagnosedAtEveryCallOnTheCycle(string assemblyName, string declarations, string members, string body, string[] expectedCalls)
    {
        AssertReportsOnlyAt(Shader(declarations, members, body), assemblyName, expectedCalls);
    }

    /// <summary>
    /// The message names what the author wrote, a local function being the case where the generated HLSL
    /// holds a name the author never wrote.
    /// </summary>
    [TestMethod]
    public void TheMessageNamesTheDeclarationTheAuthorWrote()
    {
        const string body = """
            static float Twice(float value) => Twice(value) * 2;

                        this.buffer[0] = Twice(2.0f);
            """;

        Diagnostic diagnostic = Run(Shader("", "", body), "RecursiveCallMessageTests").Diagnostics.Single();
        string message = diagnostic.GetMessage();

        Assert.IsTrue(message.Contains("Twice(float)"), message);
        Assert.IsFalse(message.Contains("__"), message);
    }

    /// <summary>
    /// Calls off any cycle, in the shapes the rows above close one through, so that the rows answer for the
    /// cycle rather than for the shape. Each is required to produce source as well, an identifier being absent
    /// from a run that generated nothing saying nothing, and the shader compiler is handed the result.
    /// </summary>
    [TestMethod]
    [DataRow(
        "ChainOfMethodsOfTheShaderTests",
        "",
        """
        private static float First(float value) => Second(value);

            private static float Second(float value) => Third(value);

            private static float Third(float value) => value * 2;
        """,
        "this.buffer[0] = First(2.0f);")]
    [DataRow(
        "MethodReachedTwiceTests",
        """
        internal static class Helper
        {
            public static float Twice(float value) => value * 2;

            public static float Left(float value) => Twice(value);

            public static float Right(float value) => Twice(value);
        }
        """,
        "private static float Both(float value) => Helper.Left(value) + Helper.Right(value);",
        "this.buffer[0] = Both(Helper.Twice(2.0f));")]
    [DataRow(
        "OverloadsCallingEachOtherTests",
        "",
        """
        private static float Twice(float value) => Twice((int)value);

            private static float Twice(int value) => value * 2;
        """,
        "this.buffer[0] = Twice(2.0f);")]
    [DataRow(
        "ConstructorCalledFromAMethodTests",
        """
        internal struct Helper
        {
            public float Amount;

            public Helper(float amount)
            {
                Amount = amount;
            }

            public static float Make(float amount) => new Helper(amount).Amount;
        }
        """,
        "",
        "this.buffer[0] = Helper.Make(2.0f);")]
    [DataRow(
        "LocalFunctionsCallingOneAnotherTests",
        "",
        "",
        """
        static float Twice(float value) => value * 2;

                    static float Four(float value) => Twice(Twice(value));

                    this.buffer[0] = Four(2.0f) + Twice(2.0f);
        """)]
    [DataRow(
        "PartialMethodOfTheShaderTests",
        """
        internal static class Helper
        {
            public static float Twice(float value) => value * 2;
        }
        """,
        """
        private static partial float Twice(float value);

            private static partial float Twice(float value) => Helper.Twice(value);
        """,
        "this.buffer[0] = Twice(2.0f);")]
    public void ACallOffACycleIsNotDiagnosed(string assemblyName, string declarations, string members, string body)
    {
        GeneratorRunResult result = Run(Shader(declarations, members, body), assemblyName);

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

    private static void AssertReportsOnlyAt(string source, string assemblyName, string[] expectedCalls)
    {
        ImmutableArray<Diagnostic> diagnostics = Run(source, assemblyName).Diagnostics;

        string[] actualIds = [.. diagnostics.Select(static diagnostic => diagnostic.Id).Distinct()];

        Assert.IsTrue(actualIds.SequenceEqual(["CMPW0129"]), $"CMPW0129 is not the only report: {string.Join(", ", actualIds)}");

        // The location is read back through the tree, so a report bound to none fails here rather than reading as a match
        string[] actualCalls = [.. diagnostics.Select(static diagnostic => diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan)).Order()];

        Assert.IsTrue(actualCalls.SequenceEqual(expectedCalls.Order()), $"The reports are not at the expected calls: {string.Join(", ", actualCalls)}");
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
