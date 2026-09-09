namespace ComputeWeave.SourceGenerators.Helpers;

/// <summary>
/// A lookup for the thread group sizes that each <see cref="DefaultThreadGroupSizes"/> value stands for.
/// </summary>
/// <remarks>
/// The generator and the analyzer both have to answer this, and they have to answer it the same way: the
/// analyzer judges a declaration that the generator then compiles. Keeping the values here rather than in
/// each of them is what makes the two answers the same by construction.
/// </remarks>
internal static class DefaultThreadGroupSizeLookup
{
    /// <summary>
    /// Tries to get the thread group sizes a given <see cref="DefaultThreadGroupSizes"/> value stands for.
    /// </summary>
    /// <param name="size">The value to resolve.</param>
    /// <param name="threadsX">The number of threads in each thread group for the X axis.</param>
    /// <param name="threadsY">The number of threads in each thread group for the Y axis.</param>
    /// <param name="threadsZ">The number of threads in each thread group for the Z axis.</param>
    /// <returns>Whether <paramref name="size"/> is one of the declared values.</returns>
    public static bool TryGetSizes(DefaultThreadGroupSizes? size, out int threadsX, out int threadsY, out int threadsZ)
    {
        (threadsX, threadsY, threadsZ) = size switch
        {
            DefaultThreadGroupSizes.X => (64, 1, 1),
            DefaultThreadGroupSizes.Y => (1, 64, 1),
            DefaultThreadGroupSizes.Z => (1, 1, 64),
            DefaultThreadGroupSizes.XY => (8, 8, 1),
            DefaultThreadGroupSizes.XZ => (8, 1, 8),
            DefaultThreadGroupSizes.YZ => (1, 8, 8),
            DefaultThreadGroupSizes.XYZ => (4, 4, 4),
            _ => (0, 0, 0)
        };

        return (threadsX, threadsY, threadsZ) is not (0, 0, 0);
    }
}
