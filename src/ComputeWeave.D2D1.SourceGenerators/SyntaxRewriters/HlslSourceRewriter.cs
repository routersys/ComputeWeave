using ComputeWeave.SourceGeneration.Extensions;
using ComputeWeave.SourceGeneration.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ComputeWeave.SourceGeneration.Diagnostics.DiagnosticDescriptors;

namespace ComputeWeave.SourceGeneration.SyntaxRewriters;

/// <inheritdoc/>
partial class HlslSourceRewriter
{
    /// <inheritdoc/>
    protected partial void TrackKnownMethodInvocation(string metadataName)
    {
        Requirements.NeedsD2DRequiresScenePositionAttribute |= HlslKnownMethods.NeedsD2DRequiresScenePositionAttribute(metadataName);
    }

    /// <summary>
    /// Reports a thread synchronization intrinsic being invoked, which the pixel shader profiles refuse.
    /// </summary>
    /// <param name="node">The invocation that is about to be written out as it stands.</param>
    /// <param name="metadataName">The fully qualified metadata name of the intrinsic being invoked.</param>
    /// <param name="method">The resolved target of <paramref name="node"/>.</param>
    /// <returns>Whether the invocation was reported, in which case the caller leaves it alone.</returns>
    /// <remarks>
    /// <para>
    /// A pixel shader runs with no thread group to synchronize, so FXC refuses the call under every pixel shader
    /// profile, and before this the failure landed on the shader type as the forwarded compiler error. Reporting
    /// here keeps the call from reaching the compiler at all, the compilation step being skipped for a shader whose
    /// rewriting produced an error, which is what lets the author see their own line.
    /// </para>
    /// <para>
    /// The report is on the shader rewriter alone. A barrier returns nothing, so no static field initializer can
    /// hold one, and a method an initializer imports is rewritten by the shader rewriter like any other.
    /// </para>
    /// </remarks>
    protected bool ReportThreadSynchronizationInPixelShader(InvocationExpressionSyntax node, string metadataName, IMethodSymbol method)
    {
        if (!HlslKnownMethods.IsThreadSynchronization(metadataName))
        {
            return false;
        }

        Diagnostics.Add(ThreadSynchronizationInPixelShader, node, method.Name);

        return true;
    }
}