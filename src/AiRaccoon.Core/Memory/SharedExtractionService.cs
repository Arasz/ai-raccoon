namespace AiRaccoon.Core.Memory;

/// <summary>
///     Mechanical shared-extraction scoring (no LLM): a provenance-archetype prior plus bounded
///     content-shape evidence produce a ranked candidate list (docs/adr/0018-promotion-scoring-v2.md).
///     Dedup is exact against the shared tier; recency is a sort tie-break only, never part of the score.
/// </summary>
public sealed class SharedExtractionService
{
    /// <summary>Per-pass candidate cap, shared by the hosted loop and the MCP tool default (single source; see docs/work/archive/2026-08-06-extraction-followups-plan.md S10).</summary>
    public const int DefaultCandidateLimit = 20;

    /// <summary>Preview length shared with the propose-tier review tool, so both truncate the same way.</summary>
    public const int PreviewLength = 300;

    /// <summary>Kept at 0.4 after re-examination for v3 (docs/adr/0018-promotion-scoring-v2.md).</summary>
    private const double CandidateFloor = 0.4;

    /// <summary>Per-source-document cap on how many chunks of one document may occupy the propose
    /// queue at once — set to 3 pending the final value from labeled data
    /// (docs/work/2026-08-09-promotion-scoring-measurement.md: one document held 33 of 965 slots).</summary>
    public const int MaxQueuedPerSourceDocument = 3;

    /// <summary>Scoring and (in promote mode) selection of rows to share, capped at `limit`. Never mutates anything.</summary>
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
        var ranked = RankAll(projectId, allProjectIds, rows, sharedValues, sharedPaths, includeTtlRows, now);
        var candidates = new List<ShareCandidate>();
        var promoted = new List<string>();
        foreach (var candidate in ranked)
        {
            if (candidates.Count >= limit)
            {
                break;
            }

            candidates.Add(candidate);
            if (mode == ExtractMode.Promote)
            {
                promoted.Add(candidate.Hash);
            }
        }

        return new ShareExtractResult(candidates, promoted);
    }

    /// <summary>Every eligible candidate (above the floor, deduped against the shared tier), ranked
    /// by score then recency — unbounded, so a caller can refresh a row's score without that row
    /// having to re-enter the top of any display limit first.</summary>
    public IReadOnlyList<ShareCandidate> RankAll(
        string projectId,
        IReadOnlyList<string> allProjectIds,
        IReadOnlyList<ExtractionCandidateRow> rows,
        IReadOnlyCollection<string> sharedValues,
        IReadOnlyCollection<string> sharedPaths,
        bool includeTtlRows,
        DateTimeOffset now)
    {
        var sharedValueSet = sharedValues.Select(v => NormalizeWhitespace(v)).ToHashSet(StringComparer.Ordinal);
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

        var ranked = new List<ShareCandidate>();
        foreach (var (row, score, reasons) in scored)
        {
            if (IsDuplicate(row, sharedValueSet, sharedPathSet))
            {
                continue;
            }

            ranked.Add(new ShareCandidate(
                row.Hash, row.Path, Truncate(row.Value), score, row.Rating, row.AccessCount,
                row.CreatedAt, reasons, row.SourceFile));
        }

        return ranked;
    }

    /// <summary>Filters a ranked, not-yet-queued candidate stream to what each source document may
    /// still admit, given how many of its chunks are already queued — keeping the highest-scoring
    /// chunks first. Rows with a null source_file are organic notes with no document to flood and are
    /// exempt (docs/work/2026-08-09-promotion-scoring-measurement.md).</summary>
    public static IReadOnlyList<ShareCandidate> CapPerSourceDocument(
        IReadOnlyList<ShareCandidate> rankedNotYetQueued, IReadOnlyDictionary<string, int> queuedCountsBySourceFile)
    {
        var counts = new Dictionary<string, int>(queuedCountsBySourceFile, StringComparer.Ordinal);
        var admitted = new List<ShareCandidate>();
        foreach (var candidate in rankedNotYetQueued)
        {
            if (candidate.SourceFile is null)
            {
                admitted.Add(candidate);
                continue;
            }

            var count = counts.GetValueOrDefault(candidate.SourceFile);
            if (count >= MaxQueuedPerSourceDocument)
            {
                continue;
            }

            counts[candidate.SourceFile] = count + 1;
            admitted.Add(candidate);
        }

        return admitted;
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
