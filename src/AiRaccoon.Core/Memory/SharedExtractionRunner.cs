namespace AiRaccoon.Core.Memory;

/// <summary>
///     The propose round-trip for one project: read candidate rows, rank via
///     <see cref="SharedExtractionService" />, and persist into the propose tier — shared by the MCP
///     tool and the background loop (docs/work/features-agent-memory/spec-issue-1.md §6.1).
/// </summary>
public sealed class SharedExtractionRunner(
    IMemoryStore store,
    SharedExtractionService extraction,
    IPromotionQueue queue,
    TimeProvider timeProvider)
{
    /// <summary>Ranks and queues one project's share-worthy entries; the shared index is a per-pass
    /// input, read once by the caller and reused across projects. Cross-project scoring always
    /// covers every known project id, fetched here rather than caller-supplied.</summary>
    public async Task<IReadOnlyList<ShareCandidate>> ProposeAsync(
        string projectId,
        SharedIndex sharedIndex,
        bool includeTtlRows,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(sharedIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await store.ExtractCandidatesAsync(projectId, includeTtlRows, cancellationToken)
            .ConfigureAwait(false);
        var allProjectIds = await store.GetProjectIdsAsync(cancellationToken).ConfigureAwait(false);
        // Every eligible row is queued (refreshing its score), not just the top `limit` returned
        // to the caller — otherwise a row ranked outside the display window is never re-scored.
        var ranked = extraction.RankAll(projectId, allProjectIds, rows,
            sharedIndex.Values, sharedIndex.Paths, includeTtlRows, timeProvider.GetUtcNow());
        if (ranked.Count > 0)
        {
            await queue.ProposeAsync(projectId, ToQueueCandidates(rows, ranked), cancellationToken)
                .ConfigureAwait(false);
        }

        return ranked.Take(limit).ToList();
    }

    /// <summary>Queue candidates carry the FULL value and the extraction score; the preview-only
    /// ShareCandidate is joined back to its source row for those fields.</summary>
    private static IReadOnlyList<QueueCandidate> ToQueueCandidates(
        IReadOnlyList<ExtractionCandidateRow> rows, IReadOnlyList<ShareCandidate> candidates)
    {
        var byHash = rows.ToDictionary(r => r.Hash, StringComparer.Ordinal);
        return candidates
            .Select(c => byHash.TryGetValue(c.Hash, out var row)
                ? new QueueCandidate(c.Hash, c.Path, row.Value, row.SourceFile, c.Score, c.Reasons)
                : new QueueCandidate(c.Hash, c.Path, c.ValuePreview, null, c.Score, c.Reasons))
            .ToList();
    }
}
