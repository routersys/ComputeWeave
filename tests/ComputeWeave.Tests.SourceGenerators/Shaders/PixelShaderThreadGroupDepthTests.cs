using ComputeWeave.SourceGenerators;
using ComputeWeave.Tests.SourceGenerators.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ComputeWeave.Tests.SourceGenerators.Shaders;

/// <summary>
/// Which thread group depths a shader writing a pixel into a target texture may declare.
/// </summary>
/// <remarks>
/// <para>
/// The dispatch for these shaders takes its extent from the target texture, which has no depth, so it asks
/// for a single thread group on the Z axis and the generated entry point compares the X and Y axes only.
/// Every thread the group holds on the Z axis therefore reaches the body again for the same pixel.
/// </para>
/// <para>
/// A shader taking its own extents is unaffected: the Z axis is one it is dispatched over, and the entry
/// point generated for it compares that axis as well. The rows below carry both kinds for that reason,
/// because a rule reading the depth alone would refuse the shaders that are entitled to it.
/// </para>
/// </remarks>
[TestClass]
public class PixelShaderThreadGroupDepthTests
{
    [TestMethod]
    [DataRow("DefaultThreadGroupSizes.Z", "PixelNamedZTests")]
    [DataRow("DefaultThreadGroupSizes.XZ", "PixelNamedXZTests")]
    [DataRow("DefaultThreadGroupSizes.YZ", "PixelNamedYZTests")]
    [DataRow("DefaultThreadGroupSizes.XYZ", "PixelNamedXYZTests")]
    public void ANamedSizeCarryingDepthIsRefusedForAPixelShaderLikeType(string size, string assemblyName)
    {
        AnalyzerHelper.AssertDiagnostics(
            new InvalidThreadGroupSizeAttributeUseAnalyzer(),
            [PixelShader(size)],
            assemblyName,
            "CMPW0128");
    }

    /// <summary>
    /// The three named sizes that leave the Z axis at one thread.
    /// </summary>
    /// <remarks>
    /// Without these rows a rule that reported every named size would pass the rows above just as well.
    /// </remarks>
    [TestMethod]
    [DataRow("DefaultThreadGroupSizes.X", "PixelNamedXTests")]
    [DataRow("DefaultThreadGroupSizes.Y", "PixelNamedYTests")]
    [DataRow("DefaultThreadGroupSizes.XY", "PixelNamedXYTests")]
    public void ANamedSizeWithoutDepthIsAcceptedForAPixelShaderLikeType(string size, string assemblyName)
    {
        AnalyzerHelper.AssertDiagnostics(
            new InvalidThreadGroupSizeAttributeUseAnalyzer(),
            [PixelShader(size)],
            assemblyName);
    }

    [TestMethod]
    public void AnExplicitDepthIsRefusedForAPixelShaderLikeType()
    {
        AnalyzerHelper.AssertDiagnostics(
            new InvalidThreadGroupSizeAttributeUseAnalyzer(),
            [PixelShader("8, 8, 4")],
            "PixelExplicitDepthTests",
            "CMPW0128");
    }

    [TestMethod]
    public void AnExplicitSizeWithoutDepthIsAcceptedForAPixelShaderLikeType()
    {
        AnalyzerHelper.AssertDiagnostics(
            new InvalidThreadGroupSizeAttributeUseAnalyzer(),
            [PixelShader("16, 4, 1")],
            "PixelExplicitFlatTests");
    }

    /// <summary>
    /// A shader taking its own extents is dispatched over the Z axis, so the depth is its to use.
    /// </summary>
    /// <remarks>
    /// This is the row that holds the rule to the kind of shader rather than to the value it declares.
    /// </remarks>
    [TestMethod]
    [DataRow("DefaultThreadGroupSizes.XYZ", "ComputeNamedXYZTests")]
    [DataRow("8, 8, 4", "ComputeExplicitDepthTests")]
    public void ADepthIsAcceptedForAShaderTakingItsOwnExtents(string size, string assemblyName)
    {
        const string Source = """
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize({0})]
            internal readonly partial struct Shader : IComputeShader
            {
                public void Execute()
                {
                }
            }
            """;

        AnalyzerHelper.AssertDiagnostics(
            new InvalidThreadGroupSizeAttributeUseAnalyzer(),
            [Source.Replace("{0}", size)],
            assemblyName);
    }

    /// <summary>
    /// A size outside the range each axis allows is answered by the bound that was already there.
    /// </summary>
    /// <remarks>
    /// The depth is read after that bound, so a value that is invalid on its own is reported once, as the
    /// invalid value it is, rather than twice. Without this row the new rule could swallow the older one.
    /// </remarks>
    [TestMethod]
    public void ADepthOutsideTheAllowedRangeIsAnsweredByTheExistingBound()
    {
        AnalyzerHelper.AssertDiagnostics(
            new InvalidThreadGroupSizeAttributeUseAnalyzer(),
            [PixelShader("8, 8, 128")],
            "PixelOutOfRangeDepthTests",
            "CMPW0044");
    }

    /// <summary>
    /// Builds a shader writing a pixel into a target texture, carrying the given thread group size.
    /// </summary>
    /// <param name="size">The arguments to give <c>[ThreadGroupSize]</c>.</param>
    /// <returns>The source for the shader.</returns>
    private static string PixelShader(string size)
    {
        return $$"""
            using ComputeWeave;

            namespace Shaders;

            [ThreadGroupSize({{size}})]
            internal readonly partial struct Shader : IComputeShader<Float4>
            {
                public Float4 Execute()
                {
                    return default;
                }
            }
            """;
    }
}
