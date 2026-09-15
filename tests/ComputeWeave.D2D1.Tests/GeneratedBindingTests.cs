using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ComputeWeave.D2D1.Descriptors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.D2D1.Tests;

[TestClass]
public partial class GeneratedBindingTests
{
    // The header the text names, which declares the entry point macro and gathers the captured values into the constant buffer
    private const string HeaderInclude = "#include \"d2d1effecthelpers.hlsli\"";

    // The entry point, built from the header's macro rather than declared as a plain function
    private const string EntryPoint = "D2D_PS_ENTRY(Execute)";

    // The input count the text states, read by the header to declare the input accessors
    private static readonly Regex InputCountDefine = new(@"^#define D2D_INPUT_COUNT (\d+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Returns the HLSL a shader type carries and the bindings its descriptor declares, without compiling anything.
    /// </summary>
    /// <typeparam name="T">The shader type to read.</typeparam>
    /// <returns>The HLSL source, the input count and the resource texture descriptions of <typeparamref name="T"/>.</returns>
    private static (string Source, int InputCount, int[] ResourceTextureIndices) Describe<T>()
        where T : unmanaged, ID2D1PixelShader, ID2D1PixelShaderDescriptor<T>
    {
        return (T.HlslSource, T.InputCount, T.ResourceTextureDescriptions.ToArray().Select(static description => description.Index).ToArray());
    }

    /// <summary>
    /// Collects every shader type in this assembly that carries a generated descriptor.
    /// </summary>
    /// <returns>The shader types, paired with the HLSL and the declared bindings each one carries.</returns>
    private static List<(Type Type, string Source, int InputCount, int[] ResourceTextureIndices)> GetGeneratedShaders()
    {
        MethodInfo definition = typeof(GeneratedBindingTests).GetMethod(
            nameof(Describe),
            BindingFlags.NonPublic | BindingFlags.Static)!;

        List<(Type, string, int, int[])> shaders = [];

        foreach (Type type in typeof(GeneratedBindingTests).Assembly.GetTypes())
        {
            // Only a value type that names itself as its own descriptor carries generated HLSL
            if (!type.IsValueType || type.ContainsGenericParameters)
            {
                continue;
            }

            bool carriesDescriptor = type.GetInterfaces().Any(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(ID2D1PixelShaderDescriptor<>) &&
                candidate.GetGenericArguments()[0] == type);

            if (!carriesDescriptor)
            {
                continue;
            }

            (string source, int inputCount, int[] resourceTextureIndices) =
                ((string, int, int[]))definition.MakeGenericMethod(type).Invoke(null, null)!;

            shaders.Add((type, source, inputCount, resourceTextureIndices));
        }

        return shaders;
    }

    /// <summary>
    /// Reports why a shader's HLSL does not state its own binding, if it does not.
    /// </summary>
    /// <param name="source">The HLSL to read.</param>
    /// <param name="inputCount">The input count the descriptor declares.</param>
    /// <param name="resourceTextureIndices">The resource texture indices the descriptor declares.</param>
    /// <returns>A description of the first problem found, or <see langword="null"/> when the text states its binding.</returns>
    private static string? DescribeBindingProblem(string source, int inputCount, int[] resourceTextureIndices)
    {
        // The header is what a caller compiling the text has to supply, so the text has to name it and nothing else in its place
        int includes = CountOccurrences(source, HeaderInclude);

        if (includes != 1)
        {
            return $"names the effect helpers header {includes} times, where a caller compiling it needs exactly one include to satisfy";
        }

        int entryPoints = CountOccurrences(source, EntryPoint);

        if (entryPoints != 1)
        {
            return $"builds {entryPoints} entry points from the header's macro, so which one a caller dispatches is not determined by the text";
        }

        Match stated = InputCountDefine.Match(source);

        if (!stated.Success || int.Parse(stated.Groups[1].Value, CultureInfo.InvariantCulture) != inputCount)
        {
            return $"states an input count of '{(stated.Success ? stated.Groups[1].Value : "nothing")}' where its descriptor declares {inputCount} inputs";
        }

        // A resource texture binds by the index the author declared, so the text has to state a register for each
        foreach (int index in resourceTextureIndices)
        {
            if (!source.Contains($"register(t{index})", StringComparison.Ordinal))
            {
                return $"declares a resource texture at index {index} without stating its register in the text";
            }
        }

        return null;
    }

    /// <summary>
    /// Counts the non-overlapping occurrences of <paramref name="value"/> in <paramref name="source"/>.
    /// </summary>
    private static int CountOccurrences(string source, string value)
    {
        int count = 0;

        for (int index = source.IndexOf(value, StringComparison.Ordinal); index >= 0; index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [TestMethod]
    public void GeneratedHlslStatesItsOwnBinding()
    {
        List<(Type Type, string Source, int InputCount, int[] ResourceTextureIndices)> shaders = GetGeneratedShaders();

        // Without this the loop below would hold over nothing and the test would pass while reading no shader
        Assert.IsTrue(shaders.Count > 0, "No shader type in this assembly carries a generated descriptor, so nothing was read.");

        foreach ((Type type, string source, int inputCount, int[] resourceTextureIndices) in shaders)
        {
            string? problem = DescribeBindingProblem(source, inputCount, resourceTextureIndices);

            Assert.IsNull(problem, $"{type.Name} {problem}.");
        }
    }

    [TestMethod]
    public void TheBindingCheckReportsABrokenBinding()
    {
        List<(Type Type, string Source, int InputCount, int[] ResourceTextureIndices)> shaders = GetGeneratedShaders();

        Assert.IsTrue(shaders.Count > 0, "No shader type in this assembly carries a generated descriptor, so nothing was read.");

        // Plant into real generated output rather than a synthetic string, so the check is exercised on what it guards
        (_, string intact, int inputCount, _) = shaders.OrderBy(static shader => shader.Type.FullName, StringComparer.Ordinal).First();

        Assert.IsNull(
            DescribeBindingProblem(intact, inputCount, []),
            "Untouched generated output is reported as broken, so the checks below would report anything.");

        AssertReported(
            intact.Replace(HeaderInclude, "", StringComparison.Ordinal),
            inputCount,
            [],
            "a text that never names the effect helpers header");

        AssertReported(
            intact.Replace(EntryPoint, "float4 Execute()", StringComparison.Ordinal),
            inputCount,
            [],
            "an entry point declared as a plain function");

        AssertReported(
            intact,
            inputCount + 1,
            [],
            "an input count the descriptor does not declare");

        // The register check needs a shader that declares a resource texture, so its intact text is the control for the last plant
        (_, string withTexture, int withTextureInputCount, int[] withTextureIndices) = shaders
            .Where(static shader => shader.ResourceTextureIndices.Length > 0)
            .OrderBy(static shader => shader.Type.FullName, StringComparer.Ordinal)
            .First();

        Assert.IsNull(
            DescribeBindingProblem(withTexture, withTextureInputCount, withTextureIndices),
            "Untouched generated output with a resource texture is reported as broken, so the check below would report anything.");

        AssertReported(
            withTexture.Replace($"register(t{withTextureIndices[0]})", "register(t)", StringComparison.Ordinal),
            withTextureInputCount,
            withTextureIndices,
            "a resource texture without a stated register");
    }

    /// <summary>
    /// Asserts that a planted break is reported, so the check is known not to accept everything.
    /// </summary>
    /// <param name="source">The HLSL carrying the planted break.</param>
    /// <param name="inputCount">The input count to check the text against.</param>
    /// <param name="resourceTextureIndices">The resource texture indices to check the text against.</param>
    /// <param name="planted">What was planted, for the failure message.</param>
    private static void AssertReported(string source, int inputCount, int[] resourceTextureIndices, string planted)
    {
        Assert.IsNotNull(
            DescribeBindingProblem(source, inputCount, resourceTextureIndices),
            $"The check accepts {planted}, so it would not notice that regression.");
    }
}
