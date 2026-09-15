using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ComputeWeave.Core.Intrinsics;
using ComputeWeave.D2D1;

namespace ComputeWeave.SourceGeneration.Mappings;

/// <inheritdoc/>
partial class HlslKnownMethods
{
    /// <summary>
    /// Checks whether or not a method name (previous matched with <see cref="TryGetMappedName(string, out string?)"/>
    /// needs the <c>[D2DRequiresScenePosition]</c> annotation on its containing shader in order to be used.
    /// </summary>
    /// <param name="name">The fully qualified metadata name.</param>
    /// <returns>Whether the method needs the <c>[D2DRequiresScenePosition]</c> annotation.</returns>
    public static bool NeedsD2DRequiresScenePositionAttribute(string name)
    {
        return name is "ComputeWeave.D2D1.D2D.GetScenePosition" or "ComputeWeave.D2D1.D2D.SampleInputAtPosition";
    }

    /// <summary>
    /// Checks whether or not a method name (previous matched with <see cref="TryGetMappedName(string, out string?)"/>)
    /// is a thread synchronization intrinsic, which the pixel shader profiles refuse.
    /// </summary>
    /// <param name="name">The fully qualified metadata name.</param>
    /// <returns>Whether the method is a thread synchronization intrinsic.</returns>
    /// <remarks>
    /// The shared <c>Hlsl</c> type declares six barriers for the compute shaders. Five of them were measured to be
    /// refused by FXC with <c>X3664</c> under every profile Direct2D accepts, from <c>ps_4_0_level_9_1</c> to <c>ps_5_0</c>.
    /// <c>DeviceMemoryBarrier</c> compiles under <c>ps_4_0</c>, <c>ps_4_1</c> and <c>ps_5_0</c> and is refused only under
    /// the two level 9 profiles, and the rewriting does not know the profile, so it is not listed and FXC answers for it there.
    /// </remarks>
    public static bool IsThreadSynchronization(string name)
    {
        return name is
            "ComputeWeave.Hlsl.AllMemoryBarrier" or
            "ComputeWeave.Hlsl.AllMemoryBarrierWithGroupSync" or
            "ComputeWeave.Hlsl.DeviceMemoryBarrierWithGroupSync" or
            "ComputeWeave.Hlsl.GroupMemoryBarrier" or
            "ComputeWeave.Hlsl.GroupMemoryBarrierWithGroupSync";
    }

    /// <summary>
    /// Checks whether or not a method name (previous matched with <see cref="TryGetMappedName(string, out string?)"/>)
    /// maps to a function-like macro from <c>d2d1effecthelpers.hlsli</c> that requires its coordinate argument (ie. the
    /// second argument) to be parenthesized. This is needed because those macros paste their arguments into expressions
    /// without parenthesizing them, which would otherwise break the expression semantics with compound arguments.
    /// </summary>
    /// <param name="name">The fully qualified metadata name.</param>
    /// <returns>Whether the method needs its coordinate argument to be parenthesized.</returns>
    public static bool NeedsParenthesizedCoordinateArgument(string name)
    {
        return name is
            "ComputeWeave.D2D1.D2D.SampleInput" or
            "ComputeWeave.D2D1.D2D.SampleInputAtOffset" or
            "ComputeWeave.D2D1.D2D.SampleInputAtPosition";
    }

    /// <inheritdoc/>
    private static partial Dictionary<string, string?> BuildKnownResourceSamplers()
    {
        return new()
        {
            [$"ComputeWeave.D2D1.D2D1ResourceTexture1D`1.Sample({typeof(float).FullName})"] = null,
            [$"ComputeWeave.D2D1.D2D1ResourceTexture2D`1.Sample({typeof(float).FullName}, {typeof(float).FullName})"] = "float2",
            [$"ComputeWeave.D2D1.D2D1ResourceTexture2D`1.Sample({typeof(Float2).FullName})"] = null,
            [$"ComputeWeave.D2D1.D2D1ResourceTexture3D`1.Sample({typeof(float).FullName}, {typeof(float).FullName}, {typeof(float).FullName})"] = "float3",
            [$"ComputeWeave.D2D1.D2D1ResourceTexture3D`1.Sample({typeof(Float3).FullName})"] = null
        };
    }

    /// <inheritdoc/>

    static partial void AddKnownMethods(IDictionary<string, string> knownMethods)
    {
        // Programmatically load mappings from the D2D1 class as well
        foreach (MethodInfo? method in
            from method in typeof(D2D).GetMethods(BindingFlags.Public | BindingFlags.Static)
            group method by method.Name
            into groups
            select groups.First())
        {
            string hlslName = method.GetCustomAttribute<HlslIntrinsicNameAttribute>()?.Name ?? method.Name;

            knownMethods.Add($"{typeof(D2D).FullName}{Type.Delimiter}{method.Name}", hlslName);
        }
    }
}