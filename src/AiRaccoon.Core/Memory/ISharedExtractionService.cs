namespace AiRaccoon.Core.Memory;

public interface ISharedExtractionService
{
    /// <summary>Scoring and (in promote mode) selection of rows to share, capped at `limit`. Never mutates anything.</summary>
    ShareExtractResult Run(
        ExtractMode mode,
        string projectId,
        IReadOnlyList<string> allProjectIds,
        IReadOnlyList<ExtractionCandidateRow> rows,
        IReadOnlyCollection<string> sharedValues,
        IReadOnlyCollection<string> sharedPaths,
        bool includeTtlRows,
        int limit,
        DateTimeOffset now);

    /// <summary>
    ///     Every eligible candidate (above the floor, deduped against the shared tier), ranked
    ///     by score then recency — unbounded, so a caller can refresh a row's score without that row
    ///     having to re-enter the top of any display limit first.
    /// </summary>
    IReadOnlyList<ShareCandidate> RankAll(
        string projectId,
        IReadOnlyList<string> allProjectIds,
        IReadOnlyList<ExtractionCandidateRow> rows,
        IReadOnlyCollection<string> sharedValues,
        IReadOnlyCollection<string> sharedPaths,
        bool includeTtlRows,
        DateTimeOffset now);
}
