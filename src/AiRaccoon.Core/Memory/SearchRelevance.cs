using AiRaccoon.Core.Memory.Fusion;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     The served rows after the absolute-relevance judgement, and whether their ranking may be
///     read as relevance at all.
/// </summary>
public sealed record RelevanceJudgement(IReadOnlyList<MemorySearchResult> Results, bool Unranked);

/// <summary>
///     "Is anything here actually relevant?" — the absolute signal ADR-0047 deferred: a floor on
///     the fused content cosine that a row containing every query term is exempt from, plus the
///     explicit unranked marker for rankings with no absolute backing.
/// </summary>
public static class SearchRelevance
{
    /// <summary>The wire name of the relative floor (SearchQuery.MinRelativeScore), for the truncation marker.</summary>
    public const string RelativeFloorName = "minRelativeScore";

    /// <summary>The wire name of the absolute floor below, for the truncation marker.</summary>
    public const string AbsoluteRelevanceFloorName = "absoluteRelevance";
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
    ///     Applies the absolute floor (a row whose cosine falls below it is dropped unless its text
    ///     contains every query term) and computes the unranked marker: a flat single-leg margin
    ///     with no absolutely backed row. minRelativeScore 0 disables every score floor.
    /// </summary>
    public static RelevanceJudgement Judge(
        IReadOnlyList<MemorySearchResult> results,
        IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash,
        FusionStats? stats,
        double minRelativeScore,
        IReadOnlySet<string>? allTermsMatched = null)
    {
        Guard.IsNotNull(results);

        var rows = minRelativeScore > 0
            ? (IReadOnlyList<MemorySearchResult>)[.. results.Where(row => !BelowAbsoluteFloor(row, evidenceByHash, allTermsMatched))]
            : results;
        var unranked = rows.Count > 0 && IsFlatSingleLeg(stats) && !rows.Any(row => HasAbsoluteBacking(row, evidenceByHash, allTermsMatched));
        return new RelevanceJudgement(rows, unranked);
    }

    private static double? CosineOf(MemorySearchResult result, IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash) =>
        evidenceByHash is not null && evidenceByHash.TryGetValue(result.Hash, out var evidence) ? evidence.Cosine : null;

    private static bool MatchesAllTerms(MemorySearchResult result, IReadOnlySet<string>? allTermsMatched) =>
        allTermsMatched is not null && allTermsMatched.Contains(result.Hash);

    private static bool BelowAbsoluteFloor(MemorySearchResult result, IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash,
        IReadOnlySet<string>? allTermsMatched) =>
        !MatchesAllTerms(result, allTermsMatched) && CosineOf(result, evidenceByHash) is { } cosine && cosine < AbsoluteRelevanceFloor;

    private static bool HasAbsoluteBacking(MemorySearchResult result, IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash,
        IReadOnlySet<string>? allTermsMatched) =>
        MatchesAllTerms(result, allTermsMatched) || CosineOf(result, evidenceByHash) is { } cosine && cosine >= AbsoluteRelevanceFloor;

    private static bool IsFlatSingleLeg(FusionStats? stats) =>
        stats is { ParticipatingLegs.Count: 1, TopMargin: { } margin } && margin < FlatTopMargin;
}
