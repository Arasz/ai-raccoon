using AiRaccoon.Core.Memory.Fusion;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     The served rows after K6's absolute-relevance judgement, and whether their ranking may be
///     read as relevance at all.
/// </summary>
public sealed record RelevanceJudgement(IReadOnlyList<MemorySearchResult> Results, bool Unranked);

/// <summary>
///     "Is anything here actually relevant?" — the absolute signal ADR-0047 deferred: a floor on
///     the fused content cosine, plus the explicit unranked marker for rankings with no absolute
///     backing. Pure decisions over served rows and their evidence (docs/adr/0047 follow-up, K6).
/// </summary>
public static class SearchRelevance
{
    /// <summary>
    ///     Below this cosine a row is not relevant. Splits the gap the review measured: a
    ///     zero-overlap query's best row reached 0.074 while the bundled MiniLM family scores
    ///     related prose at 0.45 and above.
    /// </summary>
    public const double AbsoluteRelevanceFloor = 0.35;

    /// <summary>
    ///     A top margin below this is flat — best of a bad lot. The review measured 0.0161 for the
    ///     bad-lot response against 0.508 for a genuine match on the same bank.
    /// </summary>
    public const double FlatTopMargin = 0.1;

    /// <summary>
    ///     Applies the absolute floor (rows whose measured cosine falls below it are dropped) and
    ///     computes the unranked marker: served rows with a flat single-leg margin and no row
    ///     clearing the absolute floor. minRelativeScore 0 is ADR-0096's full-recall hatch and
    ///     disables every score floor, so that call keeps every ranked row (and stays marked).
    /// </summary>
    public static RelevanceJudgement Judge(
        IReadOnlyList<MemorySearchResult> results,
        IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash,
        FusionStats? stats,
        double minRelativeScore)
    {
        Guard.IsNotNull(results);

        var rows = minRelativeScore > 0
            ? (IReadOnlyList<MemorySearchResult>)[.. results.Where(row => !BelowAbsoluteFloor(row, evidenceByHash))]
            : results;
        var unranked = rows.Count > 0 && IsFlatSingleLeg(stats) && !rows.Any(row => ClearsAbsoluteFloor(row, evidenceByHash));
        return new RelevanceJudgement(rows, unranked);
    }

    private static double? CosineOf(MemorySearchResult result, IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash) =>
        evidenceByHash is not null && evidenceByHash.TryGetValue(result.Hash, out var evidence) ? evidence.Cosine : null;

    private static bool BelowAbsoluteFloor(MemorySearchResult result, IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash) =>
        CosineOf(result, evidenceByHash) is { } cosine && cosine < AbsoluteRelevanceFloor;

    private static bool ClearsAbsoluteFloor(MemorySearchResult result, IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash) =>
        CosineOf(result, evidenceByHash) is { } cosine && cosine >= AbsoluteRelevanceFloor;

    private static bool IsFlatSingleLeg(FusionStats? stats) =>
        stats is { ParticipatingLegs.Count: 1, TopMargin: { } margin } && margin < FlatTopMargin;
}
