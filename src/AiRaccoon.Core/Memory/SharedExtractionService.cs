namespace AiRaccoon.Core.Memory;

/// <summary>
///     Mechanical shared-extraction scoring (no LLM): organic writes, cross-project references,
///     usage and recency produce a ranked candidate list; dedup is exact (value/path) against the
///     existing shared tier. Pure — the store feeds rows, the caller promotes.
/// </summary>
public sealed class SharedExtractionService
{
    /// <summary>Per-pass candidate cap, shared by the hosted loop and the MCP tool default (single source; see docs/work/2026-08-06-extraction-followups-plan.md S10).</summary>
    public const int DefaultCandidateLimit = 20;

    private const int PreviewLength = 300;
    private const double CandidateFloor = 1.0;

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

            var reasons = new List<string>();
            var score = 0.0;
            if (row.TtlDays is not null)
            {
                reasons.Add("ttl-row");
            }

            if (string.IsNullOrEmpty(row.SourceFile))
            {
                score += 2;
                reasons.Add("organic-write");
            }

            if (MentionsOtherProject(row, projectId, allProjectIds))
            {
                score += 2;
                reasons.Add("cross-project");
            }

            if (row.AccessCount > 0 || row.Rating > 0.5)
            {
                score += 1;
                reasons.Add("accessed");
            }

            if (row.CreatedAt >= now.AddDays(-30))
            {
                score += 0.5;
                reasons.Add("recent");
            }

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
        foreach (var (row, _, reasons) in scored)
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
                row.Hash, row.Path, Truncate(row.Value), row.Rating, row.AccessCount, row.CreatedAt, reasons));
            if (mode == ExtractMode.Promote)
            {
                promoted.Add(row.Hash);
            }
        }

        return new ShareExtractResult(candidates, promoted);
    }

    private static bool MentionsOtherProject(
        ExtractionCandidateRow row, string projectId, IReadOnlyList<string> allProjectIds)
    {
        foreach (var other in allProjectIds)
        {
            if (string.Equals(other, projectId, StringComparison.Ordinal))
            {
                continue;
            }

            if (row.Value.Contains(other, StringComparison.OrdinalIgnoreCase) ||
                row.SourceFile?.Contains(other, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
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
