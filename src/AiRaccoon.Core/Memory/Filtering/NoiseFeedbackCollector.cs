using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>
///     Feeds a labelled-negative content sample into the clustering service (ADR-0039) — the wiring
///     the original subsystem never had. Callers: a write a noise policy rejected (a labelled
///     negative by construction), with the deterministic <c>HermesProcessNoisePolicy</c>'s own hits
///     as the bootstrap source where Hermes is present.
///     <para>
///     Orthogonality-gated promotion from "candidate" to "active" (the original design's other
///     uncalibrated threshold, 0.75) is deliberately not restored here — it has no caller without a
///     promotion mechanism this pass does not build, and restoring an uncalled method with a magic
///     number would repeat exactly the failure ADR-0033 diagnosed. See docs/adr/0039.
///     </para>
/// </summary>
public sealed class NoiseFeedbackCollector(
    OnlineNoiseClusteringService clusteringService,
    IContentEmbedder embedder)
{
    public async Task ProcessFeedbackAsync(
        string projectId,
        string? userId,
        string sampleContent,
        string sourceChannel,
        double clusterDistanceThreshold,
        float[]? precomputedVector = null,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        Guard.IsNotNullOrWhiteSpace(sampleContent);

        if (IsImmuneContent(sampleContent))
        {
            return;
        }

        var vector = precomputedVector ?? await embedder.EmbedContentAsync(sampleContent, cancellationToken).ConfigureAwait(false);
        if (vector is null)
        {
            return;
        }

        var candidate = new NoiseCandidate(projectId, userId, sampleContent, vector);
        await clusteringService.ProcessCandidateAsync(candidate, clusterDistanceThreshold, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shared, watch-synced docs, and ADRs are immune from noise learning — curated content must never become a training negative.</summary>
    public static bool IsImmuneContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        var trimmed = content.TrimStart();
        return trimmed.StartsWith("# ADR", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("ADR-", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("# Architecture", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("scope = 'shared'", StringComparison.OrdinalIgnoreCase);
    }
}
