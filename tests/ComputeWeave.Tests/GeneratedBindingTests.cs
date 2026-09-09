using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ComputeWeave.Descriptors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests;

[TestClass]
public partial class GeneratedBindingTests
{
    // A cbuffer declaration carrying an explicit register, whatever it is named and whichever slot it takes
    private static readonly Regex ConstantBuffer = new(@"cbuffer\s+\S+\s*:\s*register\(b\d+\)", RegexOptions.Compiled);

    // The entry point marker and the function under it, matched without regard to spelling so a miscased one is still found
    private static readonly Regex EntryPoint = new(@"^\[(numthreads)\(([^)]*)\)\]\r?\n\s*\S+\s+(\w+)\s*\(", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns the HLSL a shader type carries, without compiling it.
    /// </summary>
    /// <typeparam name="T">The shader type to read.</typeparam>
    /// <returns>The HLSL source the generator wrote for <typeparamref name="T"/>.</returns>
    private static string GetHlslSource<T>()
        where T : struct, IComputeShaderDescriptor<T>
    {
        return T.HlslSource;
    }

    /// <summary>
    /// Collects every shader type in this assembly that carries a generated descriptor.
    /// </summary>
    /// <returns>The shader types, paired with the HLSL each one carries.</returns>
    private static List<(Type Type, string Source)> GetGeneratedShaders()
    {
        MethodInfo definition = typeof(GeneratedBindingTests).GetMethod(
            nameof(GetHlslSource),
            BindingFlags.NonPublic | BindingFlags.Static)!;

        List<(Type, string)> shaders = [];

        foreach (Type type in typeof(GeneratedBindingTests).Assembly.GetTypes())
        {
            // Only a value type that names itself as its own descriptor carries generated HLSL
            if (!type.IsValueType || type.ContainsGenericParameters)
            {
                continue;
            }

            bool carriesDescriptor = type.GetInterfaces().Any(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IComputeShaderDescriptor<>) &&
                candidate.GetGenericArguments()[0] == type);

            if (!carriesDescriptor)
            {
                continue;
            }

            shaders.Add((type, (string)definition.MakeGenericMethod(type).Invoke(null, null)!));
        }

        return shaders;
    }

    /// <summary>
    /// Reports why a shader's HLSL does not state its own binding, if it does not.
    /// </summary>
    /// <param name="source">The HLSL to read.</param>
    /// <returns>A description of the first problem found, or <see langword="null"/> when the text states its binding.</returns>
    private static string? DescribeBindingProblem(string source)
    {
        Match entryPoint = EntryPoint.Match(source);

        if (!entryPoint.Success)
        {
            return "does not state an entry point, so a caller cannot find what to dispatch";
        }

        if (EntryPoint.Matches(source).Count != 1)
        {
            return "states more than one entry point, so which one a caller dispatches is not determined by the text";
        }

        // FXC declares only the lowercase spelling, so any other casing closes the Direct3D 11 route
        if (entryPoint.Groups[1].Value != "numthreads")
        {
            return $"spells the entry point marker as '{entryPoint.Groups[1].Value}', which FXC refuses";
        }

        if (entryPoint.Groups[3].Value != "Execute")
        {
            return $"names its entry point '{entryPoint.Groups[3].Value}', which the documented contract does not";
        }

        // The thread group size has to be readable from the text, not recovered from the generator
        foreach (string argument in entryPoint.Groups[2].Value.Split(','))
        {
            string name = argument.Trim();

            if (!source.Contains($"#define {name} ", StringComparison.Ordinal))
            {
                return $"reads {name} in its entry point marker without defining it in the same text";
            }
        }

        if (!ConstantBuffer.IsMatch(source))
        {
            return "does not declare a constant buffer with a register, so a caller cannot tell what to bind or where";
        }

        return null;
    }

    [TestMethod]
    public void Verify_GeneratedHlslStatesItsOwnBinding()
    {
        List<(Type Type, string Source)> shaders = GetGeneratedShaders();

        // Without this the loop below would hold over nothing and the test would pass while reading no shader
        Assert.IsTrue(shaders.Count > 0, "No shader type in this assembly carries a generated descriptor, so nothing was read.");

        foreach ((Type type, string source) in shaders)
        {
            string? problem = DescribeBindingProblem(source);

            Assert.IsNull(problem, $"{type.Name} {problem}.");
        }
    }

    [TestMethod]
    public void Verify_TheBindingCheckReportsABrokenBinding()
    {
        List<(Type Type, string Source)> shaders = GetGeneratedShaders();

        Assert.IsTrue(shaders.Count > 0, "No shader type in this assembly carries a generated descriptor, so nothing was read.");

        // Plant into real generated output rather than a synthetic string, so the check is exercised on what it guards
        string intact = shaders.OrderBy(static shader => shader.Type.FullName, StringComparer.Ordinal).First().Source;

        Assert.IsNull(
            DescribeBindingProblem(intact),
            "Untouched generated output is reported as broken, so the checks below would report anything.");

        AssertReported(
            intact.Replace("[numthreads(", "[NumThreads(", StringComparison.Ordinal),
            "an entry point marker FXC refuses");

        AssertReported(
            intact.Replace(" Execute(", " Dispatch(", StringComparison.Ordinal),
            "an entry point under a different name");

        AssertReported(
            intact.Replace("#define __GroupSize__get_X ", "#define __NotTheGroupSize ", StringComparison.Ordinal),
            "a thread group size the text never defines");

        AssertReported(
            ConstantBuffer.Replace(intact, "cbuffer _"),
            "a constant buffer without a register");
    }

    /// <summary>
    /// Asserts that a planted break is reported, so the check is known not to accept everything.
    /// </summary>
    /// <param name="source">The HLSL carrying the planted break.</param>
    /// <param name="planted">What was planted, for the failure message.</param>
    private static void AssertReported(string source, string planted)
    {
        Assert.IsNotNull(
            DescribeBindingProblem(source),
            $"The check accepts {planted}, so it would not notice that regression.");
    }
}
