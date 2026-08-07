namespace AiRaccoon.Core.Memory;

/// <summary>
///     The propose round-trip for one project: read its candidate rows, rank them with
///     <see cref="SharedExtractionService" />, and persist the ranked candidates into the propose
///     tier. Owned here so the MCP tool and the background loop share one pipeline
///     (see docs/work/features-agent-memory/spec-issue-1.md §6.1).
/// </summary>
public sealed class SharedExtractionRunner(
    IMemoryStore store,
    SharedExtractionService extraction,
    IPromotionQueue queue,
    TimeProvider timeProvider)
{
    /// <summary>Ranks and queues one project's share-worthy entries; the shared index is a per-pass
    /// input, read once by the caller and reused across the projects in <paramref name="scope" />.</summary>
    public async Task<IReadOnlyList<ShareCandidate>> ProposeAsync(
        string projectId,
        IReadOnlyList<string> scope,
        SharedIndex sharedIndex,
        bool includeTtlRows,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sharedIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await store.ExtractCandidatesAsync(projectId, includeTtlRows, cancellationToken)
            .ConfigureAwait(false);
        var result = extraction.Run(ExtractMode.Propose, projectId, scope, rows,
            sharedIndex.Values, sharedIndex.Paths, includeTtlRows, limit, timeProvider.GetUtcNow());
        if (result.Candidates.Count > 0)
        {
            await queue.ProposeAsync(projectId, ToQueueCandidates(rows, result.Candidates), cancellationToken)
                .ConfigureAwait(false);
        }

        return result.Candidates;
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
