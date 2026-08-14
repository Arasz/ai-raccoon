using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>
///     Feeds a labelled-negative content sample into <see cref="INoiseClusterStore" /> (ADR-0039) —
///     the wiring the original subsystem never had. Callers: a write a noise policy rejected (a
///     labelled negative by construction), with the deterministic <c>HermesProcessNoisePolicy</c>'s
///     own hits as the bootstrap source where Hermes is present.
///     <para>
///     Deliberately append-only: each sample becomes its own row (a fresh, unique label, frequency
///     1) rather than being assigned to or merged with an existing one. Nearest-neighbour assignment
///     is exactly the scoring behaviour ADR-0039 defers — measured, on real traffic, to memorise
///     rather than generalise (93% singleton clusters). What a future <see cref="INoiseDetector" />
///     does with the accumulated samples is that detector's decision, not this collector's.
///     </para>
/// </summary>
public sealed class NoiseFeedbackCollector(
    INoiseClusterStore store,
    IContentEmbedder embedder)
{
    public async Task ProcessFeedbackAsync(
        string projectId,
        string? userId,
        string sampleContent,
        string sourceChannel,
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

        var label = "noise_sample_" + Guid.NewGuid().ToString("N")[..12];
        var sample = new NoiseCluster(
            Id: 0,
            ProjectId: projectId,
            UserId: userId,
            ClusterLabel: label,
            SampleContent: sampleContent,
            Frequency: 1,
            Status: "candidate",
            CentroidEmbedding: vector);

        await store.UpsertClusterAsync(sample, cancellationToken).ConfigureAwait(false);
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
