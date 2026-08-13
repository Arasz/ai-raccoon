using AiRaccoon.Core.Memory.Promotion;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     The propose round-trip for one project: read candidate rows, rank via
///     <see cref="SharedExtractionService" />, and persist into the propose tier — shared by the MCP
///     tool and the background loop (docs/work/features-agent-memory/spec-issue-1.md §6.1).
/// </summary>
public sealed class SharedExtractionRunner(
    IMemoryStore store,
    ISharedExtractionService extraction,
    IPromotionQueue queue,
    TimeProvider timeProvider,
    IPromotionClassifier classifier) : ISharedExtractionRunner
{
    /// <summary>
    ///     The propose round-trip for one project: read candidate rows, rank via
    ///     <see cref="SharedExtractionService" />, and persist into the propose tier — shared by the MCP
    ///     tool and the background loop (docs/work/features-agent-memory/spec-issue-1.md §6.1).
    /// </summary>
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

        // Auto-clear before ranking (ADR-0018): a row stamped by a retired scorer version is
        // deleted rather than re-scored in place — it may no longer be a candidate at all, and the
        // ranking below re-admits anything still eligible on merit in this same pass.
        await queue.ClearStaleAsync(projectId, PromotionScorer.Version, cancellationToken).ConfigureAwait(false);

        var rows = await store.ExtractCandidatesAsync(projectId, includeTtlRows, cancellationToken).ConfigureAwait(false);
        rows = await FilterByClassifierAsync(projectId, rows, cancellationToken).ConfigureAwait(false);
        var allProjectIds = await store.GetProjectIdsAsync(cancellationToken).ConfigureAwait(false);
        var ranked = extraction.RankAll(projectId, allProjectIds, rows,
            sharedIndex.Values, sharedIndex.Paths, includeTtlRows, timeProvider.GetUtcNow());
        if (ranked.Count > 0)
        {
            // Refreshing an already-queued row and enqueueing a new one are different operations:
            // a queued row is refreshed regardless of rank, but a not-yet-queued row is bounded by
            // `limit` and the per-source-document cap, so one pass cannot insert the whole eligible
            // pool nor let one document flood the queue (docs/work/2026-08-09-promotion-scoring-measurement.md).
            var existingQueueRows = await queue.ListAsync(projectId, int.MaxValue, cancellationToken)
                .ConfigureAwait(false);
            var alreadyQueued = existingQueueRows.Select(r => r.Hash).ToHashSet(StringComparer.Ordinal);
            var queuedCountsBySourceFile = existingQueueRows
                .Where(r => r.SourceFile is not null)
                .GroupBy(r => r.SourceFile!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var notYetQueued = ranked.Where(c => !alreadyQueued.Contains(c.Hash)).ToList();
            var admissibleNew = SharedExtractionService.CapPerSourceDocument(notYetQueued, queuedCountsBySourceFile);
            var toQueue = ranked.Where(c => alreadyQueued.Contains(c.Hash))
                .Concat(admissibleNew.Take(limit))
                .ToList();
            if (toQueue.Count > 0)
            {
                await queue.ProposeAsync(projectId, ToQueueCandidates(rows, toQueue), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return [.. ranked.Take(limit)];
    }

    /// <summary>
    ///     With the opt-in ONNX promotion model enabled, drops candidates the semantic classifier
    ///     rejects before the mechanical scorer ranks them. Disabled (the default) is a pass-through —
    ///     the zero-shot vector path alone is not a promotion gate.
    /// </summary>
    private async Task<IReadOnlyList<ExtractionCandidateRow>> FilterByClassifierAsync(
        string projectId, IReadOnlyList<ExtractionCandidateRow> rows, CancellationToken cancellationToken)
    {
        if (!classifier.IsModelEnabled)
        {
            return rows;
        }

        var kept = new List<ExtractionCandidateRow>(rows.Count);
        foreach (var row in rows)
        {
            var result = await classifier.ClassifyCandidateAsync(
                    new MemoryWriteRequest(projectId, row.Value, SourceFile: row.SourceFile), cancellationToken)
                .ConfigureAwait(false);
            if (result.IsEligibleForPromotion)
            {
                kept.Add(row);
            }
        }

        return kept;
    }

    /// <summary>
    ///     Queue candidates carry the FULL value and the extraction score; the preview-only
    ///     ShareCandidate is joined back to its source row for those fields.
    /// </summary>
    private static IReadOnlyList<QueueCandidate> ToQueueCandidates(
        IReadOnlyList<ExtractionCandidateRow> rows, IReadOnlyList<ShareCandidate> candidates)
    {
        var byHash = rows.ToDictionary(r => r.Hash, StringComparer.Ordinal);
        return
        [
            .. candidates
                .Select(c => byHash.TryGetValue(c.Hash, out var row)
                    ? new QueueCandidate(c.Hash, c.Path, row.Value, row.SourceFile, c.Score, c.Reasons, PromotionScorer.Version)
                    : new QueueCandidate(c.Hash, c.Path, c.ValuePreview, null, c.Score, c.Reasons, PromotionScorer.Version))
        ];
    }
}
