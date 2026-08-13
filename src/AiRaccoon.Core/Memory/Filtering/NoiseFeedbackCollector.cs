using System;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Memory.Filtering;

public sealed class NoiseFeedbackCollector(
    OnlineNoiseClusteringService clusteringService,
    IContentEmbedder embedder)
{
    private const double MaxOrthogonalityOverlap = 0.75;

    public async Task ProcessFeedbackAsync(
        string projectId,
        string? userId,
        string sampleContent,
        string sourceChannel,
        float[]? precomputedVector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleContent);

        // Scope Immunity: shared, watch-synced docs, and ADRs are immune from noise learning
        if (IsImmuneContent(sampleContent))
        {
            return;
        }

        var vector = precomputedVector ?? await embedder.EmbedContentAsync(sampleContent, cancellationToken).ConfigureAwait(false);
        if (vector is null)
        {
            return;
        }

        var candidate = new NoiseCandidate(
            ProjectId: projectId,
            UserId: userId,
            SampleContent: sampleContent,
            Vector: vector);

        await clusteringService.ProcessCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

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

    public static bool CheckOrthogonality(float[] noiseCentroid, float[] coreCentroid)
    {
        var similarity = 1.0 - ZeroShotEmbeddingFilter.CosineDistance(noiseCentroid, coreCentroid);
        return similarity <= MaxOrthogonalityOverlap;
    }
}
