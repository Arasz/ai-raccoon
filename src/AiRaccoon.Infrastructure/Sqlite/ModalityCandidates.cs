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
        IEnumerable<IReadOnlyList<MemorySearchResult>> perContext) =>
        [
            .. perContext
                .SelectMany(list => list)
                .GroupBy(result => result.Hash, StringComparer.Ordinal)
                .Select(group => group.OrderBy(result => result.Ranking).First())
                .OrderBy(result => result.Ranking)
        ];

    public static IReadOnlyList<MemorySearchResult> ByCosine(
        IEnumerable<IReadOnlyList<MemorySearchResult>> perContext) =>
        [
            .. perContext
                .SelectMany(list => list)
                .GroupBy(result => result.Hash, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(result => result.Ranking).First())
                .OrderByDescending(result => result.Ranking)
        ];
}
