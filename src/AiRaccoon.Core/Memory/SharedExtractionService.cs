namespace AiRaccoon.Core.Memory;

/// <summary>
///     Mechanical shared-extraction scoring (no LLM): a provenance-archetype prior plus bounded
///     content-shape evidence produce a ranked candidate list (docs/adr/0018-promotion-scoring-v2.md);
///     dedup is exact (value/path) against the existing shared tier. Pure — the store feeds rows, the
///     caller promotes. Recency is a sort tie-break only, never part of the score.
/// </summary>
public sealed class SharedExtractionService
{
    /// <summary>Per-pass candidate cap, shared by the hosted loop and the MCP tool default (single source; see docs/work/2026-08-06-extraction-followups-plan.md S10).</summary>
    public const int DefaultCandidateLimit = 20;

    private const int PreviewLength = 300;

    /// <summary>Re-examined for v3 (docs/adr/0018-promotion-scoring-v2.md v3 section) and kept: every
    /// hard-noise channel prior sits below 0.4 with no content rescue, and the weakest real channel
    /// (plan, 0.70) can still fall below it under heavy ephemera — the same gap the floor was
    /// originally derived from in v2.</summary>
    private const double CandidateFloor = 0.4;

    /// <summary>Scoring and (in promote mode) selection of rows to share. Never mutates anything.</summary>
    public ShareExtractResult Run(
        ExtractMode mode,
        string projectId,
        IReadOnlyList<string> allProjectIds,
        IReadOnlyList<ExtractionCandidateRow> rows,
        IReadOnlyCollection<string> sharedValues,
        IReadOnlyCollection<string> sharedPaths,
        bool includeTtlRows,
        int limit,
        DateTimeOffset now)
    {
        var sharedValueSet = sharedValues.ToHashSet(StringComparer.Ordinal);
        var sharedPathSet = sharedPaths.ToHashSet(StringComparer.Ordinal);
        var scored = new List<(ExtractionCandidateRow Row, double Score, List<string> Reasons)>();
        foreach (var row in rows)
        {
            if (row.TtlDays is not null && !includeTtlRows)
            {
                continue;
            }

            var (score, scoreReasons) = PromotionScorer.Score(row, projectId, allProjectIds);
            var reasons = new List<string>();
            if (row.TtlDays is not null)
            {
                reasons.Add("ttl-row");
            }

            reasons.AddRange(scoreReasons);

            if (score >= CandidateFloor)
            {
                scored.Add((row, score, reasons));
            }
        }

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : b.Row.CreatedAt.CompareTo(a.Row.CreatedAt);
        });

        var candidates = new List<ShareCandidate>();
        var promoted = new List<string>();
        foreach (var (row, score, reasons) in scored)
        {
            if (IsDuplicate(row, sharedValueSet, sharedPathSet))
            {
                continue;
            }

            if (candidates.Count >= limit)
            {
                break;
            }

            candidates.Add(new ShareCandidate(
                row.Hash, row.Path, Truncate(row.Value), score, row.Rating, row.AccessCount,
                row.CreatedAt, reasons, row.SourceFile));
            if (mode == ExtractMode.Promote)
            {
                promoted.Add(row.Hash);
            }
        }

        return new ShareExtractResult(candidates, promoted);
    }

    private static bool IsDuplicate(
        ExtractionCandidateRow row, IReadOnlySet<string> sharedValues, IReadOnlySet<string> sharedPaths)
    {
        if (sharedValues.Contains(NormalizeWhitespace(row.Value)))
        {
            return true;
        }

        return sharedPaths.Contains($"shared/{row.Path}");
    }

    private static string NormalizeWhitespace(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    private static string Truncate(string value) =>
        value.Length <= PreviewLength ? value : value[..(PreviewLength - 1)] + "…";
}
