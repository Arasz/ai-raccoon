using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Cross-context modality candidate lists for global RRF fusion (see
///     docs/adr/0006-rrf-parameter-optimization.md): each dedupes by hash keeping the best
///     score, then orders by that absolute score rather than per-context rank position.
/// </summary>
internal static class ModalityCandidates
{
    public static IReadOnlyList<MemorySearchResult> ByBm25(
        IEnumerable<IReadOnlyList<MemorySearchResult>> perContext,
        IReadOnlyDictionary<string, string>? valueByHash = null) =>
    [
        .. Deduplicated(perContext, valueByHash, ascending: true)
            .OrderBy(result => result.Ranking)
    ];

    public static IReadOnlyList<MemorySearchResult> ByCosine(
        IEnumerable<IReadOnlyList<MemorySearchResult>> perContext,
        IReadOnlyDictionary<string, string>? valueByHash = null) =>
    [
        .. Deduplicated(perContext, valueByHash, ascending: false)
            .OrderByDescending(result => result.Ranking)
    ];

    /// <summary>
    ///     Groups candidates by content rather than row hash (ADR-0035): a promoted shared copy
    ///     carries a different row hash than its project original (ContentHash.Of(path, value) vs.
    ///     shared/&lt;sha256(value)&gt;.md), so scope=all would otherwise return both copies of the
    ///     same text. The project-scoped copy wins its group; a same-tier tie keeps the better score.
    ///     <paramref name="valueByHash" /> is optional — omitting it degrades to the prior per-hash
    ///     grouping (every result is already keyed by its own hash).
    /// </summary>
    private static IEnumerable<MemorySearchResult> Deduplicated(
        IEnumerable<IReadOnlyList<MemorySearchResult>> perContext,
        IReadOnlyDictionary<string, string>? valueByHash, bool ascending)
    {
        var groups = perContext
            .SelectMany(list => list)
            .GroupBy(result => ContentKeyFor(result, valueByHash), StringComparer.Ordinal);

        return ascending
            ? groups.Select(group => group.OrderBy(IsSharedCopy).ThenBy(result => result.Ranking).First())
            : groups.Select(group => group.OrderBy(IsSharedCopy).ThenByDescending(result => result.Ranking).First());
    }

    private static string ContentKeyFor(MemorySearchResult result, IReadOnlyDictionary<string, string>? valueByHash) =>
        valueByHash is not null && valueByHash.TryGetValue(result.Hash, out var value)
            ? ContentHash.OfValue(value)
            : result.Hash;

    private static bool IsSharedCopy(MemorySearchResult result) =>
        result.Path.StartsWith("shared/", StringComparison.Ordinal);
}
