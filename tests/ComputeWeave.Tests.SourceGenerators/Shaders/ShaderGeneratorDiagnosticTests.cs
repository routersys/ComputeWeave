using System.Linq;
using System.Text.RegularExpressions;
using ComputeWeave.SourceGenerators;
using ComputeWeave.Tests.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests.SourceGenerators.Shaders;

/// <summary>
/// The refusals the shader descriptor generator reports itself, rather than through an analyzer.
/// </summary>
/// <remarks>
/// <para>
/// There is one row per reporting site. Five of these identifiers are reported from two places, the rewriter
/// that walks the shader body and the one that walks a static field initializer, so a row that covers one of
/// the two would leave the other free to stop working.
/// </para>
/// <para>
/// Only the identifier is asserted. Pinning the location or the message would make every row fail on a change
/// to the wording, and what these rows exist to catch is a refusal that stops happening at all.
/// </para>
/// </remarks>
[TestClass]
public class ShaderGeneratorDiagnosticTests
{
    [TestMethod]
    [DataRow("private readonly float[] values;", "this.buffer[0] = 1;", "ShaderArrayFieldTests", "CMPW0001")]
    [DataRow("private readonly string text;", "this.buffer[0] = 1;", "ShaderManagedFieldTests", "CMPW0001")]
    [DataRow("private float Value() => ThreadIds.X;", "this.buffer[0] = Value();", "ShaderThreadIdsInAMethodTests", "CMPW0006")]
    [DataRow("private float Value() => GroupIds.X;", "this.buffer[0] = Value();", "ShaderGroupIdsInAMethodTests", "CMPW0007")]
    [DataRow("private float Value() => GroupSize.X;", "this.buffer[0] = Value();", "ShaderGroupSizeInAMethodTests", "CMPW0008")]
    [DataRow("private float Value() => GridIds.X;", "this.buffer[0] = Value();", "ShaderGridIdsInAMethodTests", "CMPW0009")]
    [DataRow("private float Value() => DispatchSize.X;", "this.buffer[0] = Value();", "ShaderDispatchSizeInAMethodTests", "CMPW0039")]
    [DataRow("private static readonly float Value = ThreadIds.X;", "this.buffer[0] = Value;", "ShaderThreadIdsInAStaticFieldTests", "CMPW0006")]
    [DataRow("private static readonly float Value = GroupIds.X;", "this.buffer[0] = Value;", "ShaderGroupIdsInAStaticFieldTests", "CMPW0007")]
    [DataRow("private static readonly float Value = GroupSize.X;", "this.buffer[0] = Value;", "ShaderGroupSizeInAStaticFieldTests", "CMPW0008")]
    [DataRow("private static readonly float Value = GridIds.X;", "this.buffer[0] = Value;", "ShaderGridIdsInAStaticFieldTests", "CMPW0009")]
    [DataRow("private static readonly float Value = DispatchSize.X;", "this.buffer[0] = Value;", "ShaderDispatchSizeInAStaticFieldTests", "CMPW0039")]
    [DataRow("private static readonly System.DateTime Value;", "this.buffer[0] = 1;", "ShaderStaticFieldTypeTests", "CMPW0038")]
    [DataRow("public float Value => 1;", "this.buffer[0] = 1;", "ShaderPropertyTests", "CMPW0040")]
    public void AShaderMemberTheGeneratorRefusesIsDiagnosed(string member, string body, string assemblyName, string expectedId)
    {
        AssertReports(Shader(member, body), assemblyName, expectedId);
    }

    /// <summary>
    /// A compute shader with no resource of its own. The generated entry point would bind nothing.
    /// </summary>
    [TestMethod]
    public void AShaderWithoutAResourceIsDiagnosed()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly float value;

                public void Execute()
                {
                }
            }
            """;

        AssertReports(Source, "ShaderWithoutResourceTests", "CMPW0005");
    }

    /// <summary>
    /// The second of the two places that report an invalid property, the field a property causes to exist.
    /// </summary>
    /// <remarks>
    /// The property itself is an explicit interface implementation, which the first place skips, so this
    /// row fails only if the second place stops reporting.
    /// </remarks>
    [TestMethod]
    public void AFieldGeneratedForAPropertyIsDiagnosed()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            internal interface INamed
            {
                int Id { get; }
            }

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader, INamed
            {
                private readonly ReadWriteBuffer<float> buffer;

                int INamed.Id { get; }

                public void Execute()
                {
                    this.buffer[ThreadIds.X] = 1;
                }
            }
            """;

        AssertReports(Source, "ShaderGeneratedPropertyFieldTests", "CMPW0040");
    }

    /// <summary>
    /// A thread group size the analyzer accepts and the shader compiler does not.
    /// </summary>
    /// <remarks>
    /// Each of the three values is inside its own range, so the refusal comes from the product exceeding
    /// what a group may hold. Reaching the compiler at all is the point: this is the identifier that carries
    /// a compiler failure back to the author.
    /// </remarks>
    [TestMethod]
    public void AShaderTheCompilerRefusesIsDiagnosed()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                [GroupShared(16384)]
                private static readonly float[] cache;

                public void Execute()
                {
                    cache[ThreadIds.X] = 1;

                    this.buffer[ThreadIds.X] = cache[ThreadIds.X];
                }
            }
            """;

        AssertReports(Source, "ShaderCompilerFailureTests", "CMPW0046");
    }

    /// <summary>
    /// A thread group the hardware cannot hold, which an analyzer refuses at the attribute.
    /// </summary>
    /// <remarks>
    /// The generator carries the same bound so that the shader never reaches the compiler. Without it the
    /// author reads two refusals for one attribute, the second of them naming a line of generated code.
    /// </remarks>
    [TestMethod]
    public void AThreadGroupWithTooManyThreadsIsNotHandedToTheCompiler()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(32, 32, 2)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                public void Execute()
                {
                    this.buffer[ThreadIds.X] = 1;
                }
            }
            """;

        AssertIsNotHandedToTheCompiler(Source, "ShaderThreadGroupTooManyThreadsCompilerTests");
    }

    /// <summary>
    /// A shader that operates on a value of double precision without declaring that it needs the support.
    /// </summary>
    /// <remarks>
    /// The width has to come from the type of a captured value. An unsuffixed literal is single precision to
    /// the compiler this generator uses, so writing one would leave the shader asking for nothing.
    /// </remarks>
    [TestMethod]
    public void AShaderNeedingDoublePrecisionWithoutTheAttributeIsDiagnosed()
    {
        AssertReports(DoublePrecisionShader("", "double"), "ShaderMissingDoublePrecisionTests", "CMPW0064");
    }

    [TestMethod]
    public void AShaderWithTheAttributeAndNoDoublePrecisionIsDiagnosed()
    {
        AssertReports(
            DoublePrecisionShader("[RequiresDoublePrecisionSupport]", "float"),
            "ShaderUnnecessaryDoublePrecisionTests",
            "CMPW0065");
    }

    /// <summary>
    /// The control. A shader that uses none of the forms above has to leave the generator silent.
    /// </summary>
    [TestMethod]
    public void AValidShaderIsNotDiagnosed()
    {
        AssertReportsNothing(Shader("private readonly float scale;", "this.buffer[0] = this.scale;"), "ShaderValidTests");
    }

    private static string Shader(string member, string body)
    {
        return $$"""
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                {{member}}

                public void Execute()
                {
                    {{body}}
                }
            }
            """;
    }

    private static string DoublePrecisionShader(string attribute, string fieldType)
    {
        return $$"""
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            {{attribute}}
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;
                private readonly {{fieldType}} factor;

                public void Execute()
                {
                    this.buffer[ThreadIds.X] = (float)(this.buffer[ThreadIds.X] * this.factor);
                }
            }
            """;
    }

    /// <summary>
    /// A static field initializer that reaches the field it initializes. C# runs the initializer once and
    /// reads the field as its default value where the cycle closes, but HLSL leaves the order of its global
    /// static initializers undefined, so the shader computes a value C# never produces.
    /// </summary>
    /// <remarks>
    /// The cycle closes through an imported declaration in both rows. An initializer reading a static field
    /// directly is handled by the rewriter for initializers, which rewrites constants alone, so it reaches
    /// neither the site this diagnostic is reported from nor the fault that site used to raise.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "StaticFieldCycleThroughAMethodTests",
        """
        internal static class Helper
        {
            public static readonly float Value = Twice();

            public static float Twice() => Value * 2;
        }
        """)]
    [DataRow(
        "StaticFieldCycleThroughAConstructorTests",
        """
        internal static class Helper
        {
            public static readonly float Value = new Box().Amount;

            public struct Box
            {
                public float Amount;

                public Box()
                {
                    Amount = Value * 2;
                }
            }
        }
        """)]
    public void AStaticFieldInitializerReachingItselfIsDiagnosed(string assemblyName, string declarations)
    {
        AssertReportsAt(CycleShader(declarations), assemblyName, "CMPW0124", 1);
    }

    /// <summary>
    /// A cycle the initializer closes through two declarations is reported at each of the two reads. The
    /// author has to change both, and a single report on the field declaration would name neither.
    /// </summary>
    [TestMethod]
    public void AStaticFieldInitializerReachingItselfTwiceIsDiagnosedAtEachRead()
    {
        const string Declarations = """
            internal static class Helper
            {
                public static readonly float Value = Twice() + Thrice();

                public static float Twice() => Value * 2;

                public static float Thrice() => Value * 3;
            }
            """;

        AssertReportsAt(CycleShader(Declarations), "StaticFieldCycleReachedTwiceTests", "CMPW0124", 2);
    }

    /// <summary>
    /// An initializer that imports a method not reading the field back is left alone. Reporting on the
    /// import itself would refuse every initializer that calls anything.
    /// </summary>
    [TestMethod]
    public void AStaticFieldInitializerImportingAMethodIsNotDiagnosed()
    {
        const string Declarations = """
            internal static class Helper
            {
                public static readonly float Value = Twice();

                public static float Twice() => 2.0f;
            }
            """;

        AssertReportsNothing(CycleShader(Declarations), "StaticFieldWithoutACycleTests");
    }

    /// <summary>
    /// An external static field read twice is written once and read twice. The entry the first read leaves
    /// behind is a completed one, and reading it again is not the initializer reaching the field it writes.
    /// </summary>
    /// <remarks>
    /// A shader reading such a field once never returns to a completed entry, so no row above measures which
    /// of the two kinds of entry the report is attached to. This one does, and it is the row that fails when
    /// the test telling them apart is inverted.
    /// </remarks>
    [TestMethod]
    public void AnExternalStaticFieldReadTwiceIsNotDiagnosed()
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            internal static class Helper
            {
                public static readonly float Value = Twice();

                public static float Twice() => 2.0f;
            }

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                public void Execute()
                {
                    this.buffer[0] = Helper.Value;
                    this.buffer[1] = Helper.Value;
                }
            }
            """;

        AssertReportsNothing(Source, "StaticFieldReadTwiceTests");
    }

    /// <summary>
    /// A static field of the shader itself whose initializer reaches the field it initializes. HLSL reads such
    /// a global as uninitialized, where C# defines the same read as the default value of the type.
    /// </summary>
    /// <remarks>
    /// A field of the shader is reached on paths of its own: neither rewriter imports the shader's own
    /// declarations, and the generator writes its static methods out before it rewrites any initializer, so
    /// there is a row per way the read can be written and per kind of declaration the initializer reaches. The
    /// last row reaches it through a constructor, which the walk follows the way it follows a method.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldCycleReadDirectlyTests",
        "private static readonly float Value = Value * 2;")]
    [DataRow(
        "ShaderStaticFieldCycleReadQualifiedTests",
        "private static readonly float Value = Shader.Value * 2;")]
    [DataRow(
        "ShaderStaticFieldCycleThroughItsOwnMethodTests",
        """
        private static readonly float Value = Twice();

            private static float Twice() => Value * 2;
        """)]
    [DataRow(
        "ShaderStaticFieldCycleThroughItsOwnMethodQualifiedTests",
        """
        private static readonly float Value = Twice();

            private static float Twice() => Shader.Value * 2;
        """)]
    [DataRow(
        "ShaderStaticFieldCycleThroughTwoOfItsOwnMethodsTests",
        """
        private static readonly float Value = Twice();

            private static float Twice() => Thrice() * 2;

            private static float Thrice() => Value * 3;
        """)]
    [DataRow(
        "ShaderStaticFieldCycleThroughAConstructorTests",
        """
        private static readonly float Value = new Box().Amount;

            public struct Box
            {
                public float Amount;

                public Box()
                {
                    Amount = Value * 2;
                }
            }
        """)]
    public void AStaticFieldOfTheShaderReachingItselfIsDiagnosed(string assemblyName, string members)
    {
        AssertReportsAt(ShaderWithStaticFields(members), assemblyName, "CMPW0124", 1);
    }

    /// <summary>
    /// A cycle a field of the shader closes through an imported declaration, once where that declaration reads
    /// the field back, and once where it calls a static method of the shader that does.
    /// </summary>
    /// <remarks>
    /// The read is written as a member access on the shader type, that being the only name an external
    /// declaration can reach the field by. The second row passes back through a static method of the shader,
    /// which is never imported, so the walk over what the initializer reaches is the only thing that sees it.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldCycleThroughAnImportTests",
        "public static float Go() => Shader.Value * 2;")]
    [DataRow(
        "ShaderStaticFieldCycleThroughAnImportedCallTests",
        "public static float Go() => Shader.Twice();")]
    public void AStaticFieldOfTheShaderReachedThroughAnImportIsDiagnosed(string assemblyName, string helper)
    {
        string source = $$"""
            using ComputeWeave;

            namespace Shaders;

            internal static class Helper
            {
                {{helper}}
            }

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                internal static readonly float Value = Helper.Go();

                private readonly ReadWriteBuffer<float> buffer;

                internal static float Twice() => Value * 2;

                public void Execute()
                {
                    this.buffer[0] = Value;
                }
            }
            """;

        AssertReportsAt(source, assemblyName, "CMPW0124", 1);
    }

    /// <summary>
    /// A cycle a field of the shader closes past its own static method, through a declaration of another type
    /// that the method calls. The second row imports that declaration from the initializer as well.
    /// </summary>
    /// <remarks>
    /// The shader's own method is written out before any initializer is rewritten, and the declaration of the
    /// other type is imported once, from that method, so the read closing the cycle passes through no rewriting
    /// of the initializer on either row: only a walk over what the initializer reaches sees it.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldCycleBeyondItsOwnMethodTests",
        """
        internal static readonly float Value = Twice();

            internal static float Twice() => Helper.Go();
        """)]
    [DataRow(
        "ShaderStaticFieldCycleBeyondAnImportedMethodTests",
        """
        internal static readonly float Value = Helper.Go() + Twice();

            internal static float Twice() => Helper.Go();
        """)]
    public void AStaticFieldOfTheShaderReachedBeyondItsOwnMethodIsDiagnosed(string assemblyName, string members)
    {
        const string Declarations = """
            internal static class Helper
            {
                public static float Go() => Shader.Value * 2;
            }
            """;

        AssertReportsAt(ShaderWithStaticFields(members, declarations: Declarations), assemblyName, "CMPW0124", 1);
    }

    /// <summary>
    /// Two or more fields of the shader whose initializers reach each other, the first reaching the second
    /// through a static method of the shader and the second reading the first back.
    /// </summary>
    /// <remarks>
    /// C# runs the first initializer to completion before the second one starts, so what it reads through the
    /// method is the second field before its initializer has run, and that read is the one report: the second
    /// initializer then reads a field whose initializer has already run. These are the shapes the shader
    /// compiler accepts: the read of a field declared later goes through a function, which is forward declared,
    /// where reading it directly does not, so nothing but this report tells the author.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldCycleThroughASecondFieldTests",
        "",
        """
        private static readonly float First = Second() + 1;

            private static readonly float Other = (First * 3) + 5;

            private static float Second() => Other * 2;
        """)]
    [DataRow(
        "ShaderStaticFieldCycleThroughAThirdFieldTests",
        "",
        """
        private static readonly float First = FromLast() + 1;

            private static readonly float Middle = First + 1;

            private static readonly float Last = Middle + 1;

            private static float FromLast() => Last * 2;
        """)]
    [DataRow(
        "ShaderStaticFieldCycleThroughAFieldReadAloneTests",
        "",
        """
        private static readonly float First = Second() + 1;

            private static readonly float Other = First;

            private static float Second() => Other * 2;
        """)]
    public void AStaticFieldOfTheShaderReachedThroughAnotherFieldIsDiagnosed(string assemblyName, string declarations, string members)
    {
        AssertReportsAt(ShaderWithStaticFields(members, "this.buffer[0] = First;", declarations), assemblyName, "CMPW0124", 1);
    }

    /// <summary>
    /// A field of the shader and a field of another type whose initializers reach each other.
    /// </summary>
    /// <remarks>
    /// Whichever type C# initializes first, its initializer starts the other one and reads its own field back
    /// through it before that initializer has run, so each of the two reads is the read a run of the program
    /// performs too early, and both are reported. Neither of the two is the shape one type can fix alone.
    /// </remarks>
    [TestMethod]
    public void AStaticFieldOfTheShaderAndAnImportedFieldReachingEachOtherAreDiagnosed()
    {
        const string Declarations = """
            internal static class Helper
            {
                public static readonly float Amount = Shader.First * 2;
            }
            """;

        const string Members = """
            internal static readonly float First = Twice();

                internal static float Twice() => Helper.Amount;
            """;

        AssertReportsAt(ShaderWithStaticFields(Members, "this.buffer[0] = First;", Declarations), "ShaderStaticFieldCycleThroughAnImportedFieldTests", "CMPW0124", 2);
    }

    /// <summary>
    /// A cycle the initializer of a field of the shader closes twice over, once by reading the field itself
    /// and once through a static method of the shader that reads it back.
    /// </summary>
    /// <remarks>
    /// Each read is a place the author has to change, so each is reported, and at a location of its own.
    /// </remarks>
    [TestMethod]
    public void AStaticFieldOfTheShaderReachingItselfTwiceIsDiagnosedAtEachRead()
    {
        const string Members = """
            private static readonly float Value = Twice() + Value;

                private static float Twice() => Value * 2;
            """;

        AssertReportsAt(ShaderWithStaticFields(Members), "ShaderStaticFieldCycleReachedTwiceTests", "CMPW0124", 2);
    }

    /// <summary>
    /// A field reaching a pair of fields whose initializers read each other. C# runs the three initializers in
    /// order, so the first reads the second before its turn and the second reads the third before its turn,
    /// while the third reads a field whose initializer has already run.
    /// </summary>
    /// <remarks>
    /// The pair read directly used to be left to the shader compiler, which refuses the read of a global
    /// declared later under an identifier of its own, naming generated code. The two reads C# performs too
    /// early are reported, and the one it does not perform too early is not.
    /// </remarks>
    [TestMethod]
    public void APairOfStaticFieldsReadingEachOtherIsDiagnosedAtTheReadsPerformedTooEarly()
    {
        const string Members = """
            private static readonly float Value = Twice();

                private static readonly float Left = Right + 1;

                private static readonly float Right = Left + 1;

                private static float Twice() => Left * 2;
            """;

        AssertReportsAt(ShaderWithStaticFields(Members), "ShaderStaticFieldPairReachedByTheWalkTests", "CMPW0124", 2);
    }

    /// <summary>
    /// The shapes the walk goes over without reaching a read performed too early: a declaration of another
    /// type called past the shader's own method, a static field of another type, an earlier field of the
    /// shader read through the method, and a later field of the shader carrying no initializer, which holds
    /// the default value of its type in C# and in the generated HLSL alike.
    /// </summary>
    /// <remarks>
    /// Every row reaches the walk, so a walk reporting on what it reaches rather than on what C# reads before
    /// its initializer has run refuses all four.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldReachingAnImportedMethodTests",
        """
        internal static class Helper
        {
            public static float Go() => 2.0f;
        }
        """,
        """
        private static readonly float Value = Twice();

            private static float Twice() => Helper.Go();
        """)]
    [DataRow(
        "ShaderStaticFieldReachingAnImportedFieldTests",
        """
        internal static class Helper
        {
            public static readonly float Amount = 5.0f;
        }
        """,
        """
        private static readonly float Value = Twice();

            private static float Twice() => Helper.Amount * 2;
        """)]
    [DataRow(
        "ShaderStaticFieldReachingAnEarlierFieldTests",
        "",
        """
        private static readonly float Base = 5.0f;

            private static readonly float Value = Twice();

            private static float Twice() => Base * 2;
        """)]
    [DataRow(
        "ShaderStaticFieldReachingALaterFieldWithoutAnInitializerTests",
        "",
        """
        private static readonly float Value = Twice();

            private static float Base;

            private static float Twice() => Base * 2;
        """)]
    public void AStaticFieldOfTheShaderReachingDeclarationsWithoutACycleIsNotDiagnosed(string assemblyName, string declarations, string members)
    {
        AssertReportsNothing(ShaderWithStaticFields(members, declarations: declarations), assemblyName);
    }

    /// <summary>
    /// The shapes that read a static field of the shader without the initializer reaching it back.
    /// </summary>
    /// <remarks>
    /// The first row is what separates a cycle from the ordinary shape: the same method reads the same field,
    /// and only the initializer reaching that method makes it one. All three are refused by a walk that reports
    /// what it reaches rather than what C# reads before its initializer has run.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldReadFromTheBodyTests",
        """
        private static readonly float Value = 2.0f;

            private static float Twice() => Value * 2;
        """,
        "this.buffer[0] = Twice();")]
    [DataRow(
        "ShaderStaticFieldInitializerCallingItsOwnMethodTests",
        """
        private static readonly float Value = Twice();

            private static float Twice() => 2.0f;
        """,
        "this.buffer[0] = Value;")]
    [DataRow(
        "ShaderStaticFieldReadingAnotherFieldTests",
        """
        private static readonly float Base = 2.0f;

            private static readonly float Value = Base * 2;
        """,
        "this.buffer[0] = Value;")]
    public void AStaticFieldOfTheShaderWithoutACycleIsNotDiagnosed(string assemblyName, string members, string body)
    {
        AssertReportsNothing(ShaderWithStaticFields(members, body), assemblyName);
    }

    /// <summary>
    /// A cycle a field closes through a declaration something else imported first: the shader body, an earlier
    /// field, or the very import that reached the field. A declaration is imported once, so the read closing
    /// the cycle is not rewritten again when the initializer calls it, and only a walk over what the
    /// initializer reaches sees it.
    /// </summary>
    /// <remarks>
    /// The rows are the declaration kinds an initializer can reach, a static method, an instance method and a
    /// constructor, and the places that import one first. The last row calls the same method twice from one
    /// initializer, which is one read and so one report.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldCycleThroughAMethodTheBodyImportedTests",
        "internal static class Helper { public static float Go() => Shader.Value * 2; }",
        "internal static readonly float Value = Helper.Go();",
        "this.buffer[0] = Helper.Go() + Value;",
        1)]
    [DataRow(
        "ShaderStaticFieldCycleThroughAMethodAnEarlierFieldImportedTests",
        "internal static class Helper { public static float Go() => Shader.Second * 2; }",
        """
        internal static readonly float First = Helper.Go();

            internal static readonly float Second = Helper.Go();
        """,
        "this.buffer[0] = First + Second;",
        1)]
    [DataRow(
        "ShaderStaticFieldCycleThroughAConstructorTheBodyImportedTests",
        """
        internal struct Box
        {
            public float Amount;

            public Box(float amount)
            {
                Amount = amount + Shader.Value;
            }

            public static float Read(Box box) => box.Amount;
        }
        """,
        "internal static readonly float Value = Box.Read(new Box(1.0f));",
        "this.buffer[0] = Box.Read(new Box(1.0f)) + Value;",
        1)]
    [DataRow(
        "ShaderStaticFieldCycleThroughAnInstanceMethodTheBodyImportedTests",
        """
        internal struct Box
        {
            public float Amount;

            public Box(float amount)
            {
                Amount = amount;
            }

            public float Read() => Amount + Shader.Value;
        }
        """,
        "internal static readonly float Value = new Box(1.0f).Read();",
        "this.buffer[0] = new Box(1.0f).Read() + Value;",
        1)]
    [DataRow(
        "ShaderStaticFieldCycleThroughAMethodCalledTwiceTests",
        "internal static class Helper { public static float Go() => Shader.Value * 2; }",
        "internal static readonly float Value = Helper.Go() + Helper.Go();",
        "this.buffer[0] = Value;",
        1)]
    public void AStaticFieldReachedThroughADeclarationImportedEarlierIsDiagnosed(string assemblyName, string declarations, string members, string body, int expectedCount)
    {
        AssertReportsAt(ShaderWithStaticFields(members, body, declarations), assemblyName, "CMPW0124", expectedCount);
    }

    /// <summary>
    /// A field of another type whose initializer calls the method that reached it, that method being in the
    /// middle of its own import when the field is imported, so its body is not rewritten again either.
    /// </summary>
    [TestMethod]
    public void AnExternalStaticFieldReachedThroughTheMethodImportingItIsDiagnosed()
    {
        const string Declarations = """
            internal static class Helper
            {
                public static readonly float Value = Go();

                public static float Go() => Value * 2;
            }
            """;

        AssertReportsAt(ShaderWithStaticFields("", "this.buffer[0] = Helper.Go();", Declarations), "ExternalStaticFieldCycleThroughTheImportingMethodTests", "CMPW0124", 1);
    }

    /// <summary>
    /// A static field read by an initializer that C# runs before the initializer of that field: a field of
    /// the same type declared after the one being initialized, read directly, through a method of the shader,
    /// through an imported method, or from the initializer of an imported field.
    /// </summary>
    /// <remarks>
    /// C# reads the default value of the type there. The shader compiler folds the initializer the later field
    /// carries when the read goes through a function, so the generated HLSL computes the initialized value
    /// instead, and it does not compile the direct read at all, naming generated code. The report names the
    /// read and the initializer that performs it too early.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldReadingALaterFieldDirectlyTests",
        "",
        """
        private static readonly float Value = Base * 2;

            private static readonly float Base = 5.0f;
        """)]
    [DataRow(
        "ShaderStaticFieldReadingALaterFieldThroughItsOwnMethodTests",
        "",
        """
        private static readonly float Value = Twice();

            private static readonly float Base = 5.0f;

            private static float Twice() => Base * 2;
        """)]
    [DataRow(
        "ShaderStaticFieldReadingALaterFieldThroughAnImportedMethodTests",
        "internal static class Helper { public static float Go() => Shader.Base * 2; }",
        """
        internal static readonly float Value = Helper.Go();

            internal static readonly float Base = 5.0f;
        """)]
    [DataRow(
        "ShaderStaticFieldReadingALaterFieldFromAnImportedInitializerTests",
        "internal static class Helper { public static readonly float Doubled = Shader.Base * 2; }",
        """
        internal static readonly float Value = Helper.Doubled;

            internal static readonly float Base = 5.0f;
        """)]
    [DataRow(
        "ExternalStaticFieldReadingALaterFieldDirectlyTests",
        """
        internal static class Helper
        {
            public static readonly float Value = Base * 2;

            public static readonly float Base = 5.0f;
        }
        """,
        "internal static readonly float Value = Helper.Value;")]
    [DataRow(
        "ExternalStaticFieldReadingALaterFieldThroughAMethodTests",
        """
        internal static class Helper
        {
            public static readonly float Value = Twice();

            public static readonly float Base = 5.0f;

            public static float Twice() => Base * 2;
        }
        """,
        "internal static readonly float Value = Helper.Value;")]
    public void AStaticFieldReadBeforeItsInitializerHasRunIsDiagnosed(string assemblyName, string declarations, string members)
    {
        AssertReportsAt(ShaderWithStaticFields(members, declarations: declarations), assemblyName, "CMPW0124", 1);
    }

    /// <summary>
    /// A read inside a local function of a method the initializer reaches. C# runs the local function only
    /// when the method calls it, so the read is performed too early only then, whereas the generated HLSL
    /// lifts the function out whether or not it is called.
    /// </summary>
    [TestMethod]
    [DataRow("ShaderStaticFieldReadInsideACalledLocalFunctionTests", "return Read();", true)]
    [DataRow("ShaderStaticFieldReadInsideAnUncalledLocalFunctionTests", "return 2.0f;", false)]
    public void AStaticFieldReadInsideALocalFunctionIsDiagnosedWhenTheFunctionIsCalled(string assemblyName, string body, bool isCalled)
    {
        string members = $$"""
            private static readonly float Value = Twice();

                private static float Twice()
                {
                    static float Read() => Value * 2;

                    {{body}}
                }
            """;

        if (isCalled)
        {
            AssertReportsAt(ShaderWithStaticFields(members), assemblyName, "CMPW0124", 1);
        }
        else
        {
            AssertReportsNothing(ShaderWithStaticFields(members), assemblyName);
        }
    }

    /// <summary>
    /// A static field written by an initializer that C# runs before the initializer of that field, through an
    /// assignment or an out argument in a method of the shader. C# discards the write when the initializer of
    /// the field runs, whereas the shader compiler folds that initializer into the declaration and keeps the
    /// write.
    /// </summary>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldWritingALaterFieldTests",
        """
        private static readonly float Value = Init();

            private static float Base = 5.0f;

            private static float Init()
            {
                Base = 2.0f;

                return 1.0f;
            }
        """)]
    [DataRow(
        "ShaderStaticFieldWritingALaterFieldThroughAnOutArgumentTests",
        """
        private static readonly float Value = Init();

            private static float Base = 5.0f;

            private static void Set(out float target)
            {
                target = 2.0f;
            }

            private static float Init()
            {
                Set(out Base);

                return 1.0f;
            }
        """)]
    public void AStaticFieldWrittenBeforeItsInitializerHasRunIsDiagnosed(string assemblyName, string members)
    {
        AssertReportsAt(ShaderWithStaticFields(members), assemblyName, "CMPW0124", 1);
    }

    /// <summary>
    /// The writes C# and the generated HLSL agree on: one to a later field carrying no initializer, which both
    /// keep, and one to the field being initialized, through an assignment or an out argument, which both lose
    /// to the initializer running after it.
    /// </summary>
    /// <remarks>
    /// The set is pinned empty rather than the identifier counted, so the rows also show the shader compiles.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ShaderStaticFieldWritingALaterFieldWithoutAnInitializerTests",
        """
        private static readonly float Value = Init();

            private static float Base;

            private static float Init()
            {
                Base = 2.0f;

                return 1.0f;
            }
        """)]
    [DataRow(
        "ShaderStaticFieldWritingItselfTests",
        """
        private static float Value = Init();

            private static float Init()
            {
                Value = 2.0f;

                return 1.0f;
            }
        """)]
    [DataRow(
        "ShaderStaticFieldWritingItselfThroughAnOutArgumentTests",
        """
        private static float Value = Init();

            private static void Set(out float target)
            {
                target = 2.0f;
            }

            private static float Init()
            {
                Set(out Value);

                return 1.0f;
            }
        """)]
    public void AStaticFieldWrittenWhereCSharpAndTheGeneratedHlslAgreeIsNotDiagnosed(string assemblyName, string members)
    {
        AssertReportsNothing(ShaderWithStaticFields(members), assemblyName);
    }

    /// <summary>
    /// The message names the field accessed and the initializer performing the access, in that order, each
    /// access being attributed to the initializer whose walk reached it.
    /// </summary>
    /// <remarks>
    /// The count and the location cannot tell the two names apart, so the messages are read.
    /// </remarks>
    [TestMethod]
    public void AStaticFieldAccessIsAttributedToTheInitializerPerformingIt()
    {
        const string Members = """
            private static readonly float First = Second + 1;

                private static readonly float Second = Third * 2;

                private static readonly float Third = 5.0f;
            """;

        string[] attributions = [.. RunDiagnostics(ShaderWithStaticFields(Members, "this.buffer[0] = First;"), "ShaderStaticFieldAccessAttributionTests")
            .Where(static diagnostic => diagnostic.Id == "CMPW0124")
            .Select(static diagnostic => Regex.Match(diagnostic.GetMessage(), @"field \S*?(\w+) is accessed while the initializer of \S*?(\w+) is running"))
            .Select(static match => $"{match.Groups[1].Value} in {match.Groups[2].Value}")
            .Order()];

        Assert.AreEqual("Second in First, Third in Second", string.Join(", ", attributions));
    }

    /// <summary>
    /// A static field a static constructor assigns: a field of another type, with or without an initializer of
    /// its own, assigned directly, through a method the constructor calls or through an out argument; a field
    /// of the shader assigned by the shader's own static constructor; and a field of the shader assigned by the
    /// static constructor of a type the body imports a method from. C# runs the constructor when its type is
    /// first touched, whereas the generated HLSL runs none and reads the initializer's value, or zero without one.
    /// </summary>
    [TestMethod]
    [DataRow(
        "ExternalStaticFieldAssignedByAStaticConstructorTests",
        """
        internal static class Helper
        {
            public static readonly float Amount;

            static Helper()
            {
                Amount = 5.0f;
            }
        }
        """,
        "internal static readonly float Value = Helper.Amount;",
        "this.buffer[0] = Value;")]
    [DataRow(
        "ExternalMutableStaticFieldAssignedByAStaticConstructorTests",
        """
        internal static class Helper
        {
            public static float Amount;

            static Helper()
            {
                Amount = 5.0f;
            }
        }
        """,
        "internal static readonly float Value = Helper.Amount;",
        "this.buffer[0] = Value;")]
    [DataRow(
        "ExternalInitializedStaticFieldOverwrittenByAStaticConstructorTests",
        """
        internal static class Helper
        {
            public static readonly float Amount = 2.0f;

            static Helper()
            {
                Amount = 5.0f;
            }
        }
        """,
        "internal static readonly float Value = Helper.Amount;",
        "this.buffer[0] = Value;")]
    [DataRow(
        "ExternalStaticFieldAssignedByAMethodAStaticConstructorCallsTests",
        """
        internal static class Helper
        {
            public static float Amount;

            static Helper()
            {
                Fill();
            }

            private static void Fill()
            {
                Amount = 5.0f;
            }
        }
        """,
        "internal static readonly float Value = Helper.Amount;",
        "this.buffer[0] = Value;")]
    [DataRow(
        "ExternalStaticFieldAssignedThroughAnOutArgumentInAStaticConstructorTests",
        """
        internal static class Helper
        {
            public static readonly float Amount;

            static Helper()
            {
                Fill(out Amount);
            }

            private static void Fill(out float target)
            {
                target = 5.0f;
            }
        }
        """,
        "internal static readonly float Value = Helper.Amount;",
        "this.buffer[0] = Value;")]
    [DataRow(
        "ShaderStaticFieldAssignedByTheShaderStaticConstructorTests",
        "",
        """
        private static readonly float Value;

            static Shader()
            {
                Value = 5.0f;
            }
        """,
        "this.buffer[0] = Value;")]
    [DataRow(
        "ShaderStaticFieldAssignedByTheStaticConstructorOfAnImportedMethodTypeTests",
        """
        internal static class Helper
        {
            static Helper()
            {
                Shader.Value = 5.0f;
            }

            public static float Go() => 2.0f;
        }
        """,
        "internal static float Value = 1.0f;",
        "this.buffer[0] = Helper.Go() + Value;")]
    public void AStaticFieldAssignedByAStaticConstructorIsDiagnosed(string assemblyName, string declarations, string members, string body)
    {
        AssertReportsAt(ShaderWithStaticFields(members, body, declarations), assemblyName, "CMPW0130", 1);
    }

    /// <summary>
    /// The static constructors that leave the fields the generated HLSL declares alone: one reading the field,
    /// one assigning a field of its type the shader does not use, and one of a type the shader imports a method
    /// from that assigns nothing the shader uses.
    /// </summary>
    /// <remarks>
    /// The set is pinned empty rather than the identifier counted, so the rows also show the shader compiles.
    /// </remarks>
    [TestMethod]
    [DataRow(
        "ExternalStaticFieldReadByAStaticConstructorTests",
        """
        internal static class Helper
        {
            public static readonly float Amount = 2.0f;

            static Helper()
            {
                _ = Amount;
            }
        }
        """,
        "internal static readonly float Value = Helper.Amount;",
        "this.buffer[0] = Value;")]
    [DataRow(
        "ExternalStaticFieldBesideOneAStaticConstructorAssignsTests",
        """
        internal static class Helper
        {
            public static readonly float Amount = 2.0f;

            public static float Other;

            static Helper()
            {
                Other = 5.0f;
            }
        }
        """,
        "internal static readonly float Value = Helper.Amount;",
        "this.buffer[0] = Value;")]
    [DataRow(
        "ImportedMethodTypeWithAStaticConstructorAssigningNothingTheShaderUsesTests",
        """
        internal static class Helper
        {
            public static float Other;

            static Helper()
            {
                Other = 5.0f;
            }

            public static float Go() => 2.0f;
        }
        """,
        "internal static readonly float Value = 1.0f;",
        "this.buffer[0] = Helper.Go() + Value;")]
    public void AStaticConstructorLeavingTheDeclaredStaticFieldsAloneIsNotDiagnosed(string assemblyName, string declarations, string members, string body)
    {
        AssertReportsNothing(ShaderWithStaticFields(members, body, declarations), assemblyName);
    }

    private static string ShaderWithStaticFields(string members, string body = "this.buffer[0] = Value;", string declarations = "")
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

    private static string CycleShader(string declarations)
    {
        return $$"""
            using ComputeWeave;

            namespace Shaders;

            {{declarations}}

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                public void Execute()
                {
                    this.buffer[0] = Helper.Value;
                }
            }
            """;
    }

    /// <summary>
    /// The refusals an attribute argument can carry. An attribute list is not written out, so what it holds
    /// cannot change the generated HLSL, and refusing it stops a build over source the author cannot correct.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is one row per identifier the argument kinds were measured to reach, the rows below carrying the
    /// same constructs with the identifier each one answers for. The rows are the refusals themselves rather
    /// than the kinds: several kinds reach one identifier, and a row per kind would say the same thing more
    /// than once while leaving an identifier free to start reporting again.
    /// </para>
    /// <para>
    /// Nothing refuses this input, so what is asserted is the whole set being empty rather than one identifier
    /// being absent, which also answers for an identifier no row below names.
    /// </para>
    /// <para>
    /// The attribute is placed on an imported method here. Placing it on a method of the shader or on an
    /// imported constructor was measured to report the same, all three being rewritten by the same walk.
    /// </para>
    /// </remarks>
    [TestMethod]
    [DataRow("\"do not use\"", "AttributeStringArgumentTests")]
    [DataRow("(object)null", "AttributeObjectArgumentTests")]
    [DataRow("new int[] { 1 }", "AttributeArrayArgumentTests")]
    [DataRow("checked(1 + 1)", "AttributeCheckedArgumentTests")]
    public void SyntaxInsideAnAttributeIsNotRefused(string argument, string assemblyName)
    {
        AssertReportsNothing(AttributeShader(argument), assemblyName);
    }

    /// <summary>
    /// The same construct written in the body it is refused in, so that the rows above answer for where the
    /// syntax sits rather than for the refusal having stopped working.
    /// </summary>
    [TestMethod]
    [DataRow("string text = \"do not use\";", "BodyStringTests", "CMPW0036")]
    [DataRow("object value = null;", "BodyObjectTests", "CMPW0050")]
    [DataRow("int[] values = new int[] { 1 };", "BodyArrayTests", "CMPW0059")]
    [DataRow("int value = checked(1 + 1);", "BodyCheckedTests", "CMPW0014")]
    public void TheSameSyntaxInABodyIsRefused(string statement, string assemblyName, string expectedId)
    {
        AssertReports(Shader("", $"{statement} this.buffer[0] = 1;"), assemblyName, expectedId);
    }

    /// <summary>
    /// A custom type holding a field of a type with no name, a pointer or a fixed size buffer, is refused as an
    /// invalid type, once. The explorer used to cast every field type to a named one and end the generator on
    /// these, whether the type is used in the body or captured by the shader.
    /// </summary>
    [TestMethod]
    [DataRow("private unsafe struct Block { public fixed float values[4]; }", "Block block = default; this.buffer[0] = 1;", "CustomTypeFixedBufferFieldTests")]
    [DataRow("private unsafe struct Node { public float value; public Node* next; }", "Node node = default; this.buffer[0] = node.value;", "CustomTypePointerFieldTests")]
    [DataRow("private unsafe struct Block { public fixed float values[4]; } private readonly Block block;", "this.buffer[0] = 1;", "CapturedCustomTypeFixedBufferFieldTests")]
    public void ACustomTypeWithAFieldOfAnUnnamedTypeIsRefused(string member, string body, string assemblyName)
    {
        AssertReportsAt(Shader(member, body), assemblyName, "CMPW0050", 1);
    }

    private static string AttributeShader(string argument)
    {
        return $$"""
            using System;
            using ComputeWeave;

            namespace Shaders;

            internal sealed class MarkAttribute : Attribute
            {
                public MarkAttribute(object value)
                {
                }
            }

            internal static class Helper
            {
                [Mark({{argument}})]
                public static float Twice(float value)
                {
                    return value * 2;
                }
            }

            [ThreadGroupSize(DefaultThreadGroupSizes.X)]
            [GeneratedComputeShaderDescriptor]
            internal readonly partial struct Shader : IComputeShader
            {
                private readonly ReadWriteBuffer<float> buffer;

                public void Execute()
                {
                    this.buffer[0] = Helper.Twice(2.0f);
                }
            }
            """;
    }

    /// <summary>
    /// Asserts that an identifier is reported a given number of times, each at a location of its own.
    /// </summary>
    /// <remarks>
    /// A report is one read the author has to change, so two reports at one location would be one read
    /// reported twice, which is what a count alone cannot tell from two reads.
    /// </remarks>
    private static void AssertReportsAt(string source, string assemblyName, string expectedId, int expectedCount)
    {
        Diagnostic[] actualDiagnostics = RunDiagnostics(source, assemblyName);
        Diagnostic[] expectedDiagnostics = [.. actualDiagnostics.Where(diagnostic => diagnostic.Id == expectedId)];

        Assert.AreEqual(
            expectedCount,
            expectedDiagnostics.Length,
            $"{expectedId} is not reported {expectedCount} time(s): {string.Join(", ", actualDiagnostics.Select(static diagnostic => diagnostic.Id).Order())}");

        Assert.AreEqual(
            expectedCount,
            expectedDiagnostics.Select(static diagnostic => diagnostic.Location.SourceSpan).Distinct().Count(),
            $"{expectedId} is reported more than once at one location: {string.Join(", ", expectedDiagnostics.Select(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition))}");
    }

    private static void AssertReports(string source, string assemblyName, string expectedId)
    {
        string[] actualIds = Run(source, assemblyName);

        Assert.IsTrue(actualIds.Contains(expectedId), $"{expectedId} is not reported: {string.Join(", ", actualIds)}");
    }

    private static void AssertReportsNothing(string source, string assemblyName)
    {
        string[] actualIds = Run(source, assemblyName);

        // A shader left alone is pinned as a set, so a compile failure arriving under its own identifier lands here
        Assert.AreEqual(0, actualIds.Length, string.Join(", ", actualIds));
    }

    private static void AssertIsNotHandedToTheCompiler(string source, string assemblyName)
    {
        string[] actualIds = Run(source, assemblyName);

        // Narrow because an analyzer answers for this shader from outside this run, and sound because a group this size would be refused if it were handed over
        Assert.IsFalse(actualIds.Contains("CMPW0046"), $"CMPW0046 is reported: {string.Join(", ", actualIds)}");
    }

    private static string[] Run(string source, string assemblyName)
    {
        // Not made distinct, so that a row can assert how many times an identifier was reported
        return [.. RunDiagnostics(source, assemblyName).Select(static diagnostic => diagnostic.Id).Order()];
    }

    private static Diagnostic[] RunDiagnostics(string source, string assemblyName)
    {
        CSharpCompilation compilation = CompilationHelper.CreateCompilation(
            [source],
            assemblyName,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        GeneratorDriver driver = GeneratorHelper.CreateDriver(new ComputeShaderDescriptorGenerator());
        GeneratorRunResult result = driver.RunGenerators(compilation).GetRunResult().Results[0];

        Assert.IsNull(result.Exception, result.Exception?.ToString());

        return [.. result.Diagnostics];
    }
}
