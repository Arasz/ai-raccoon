using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     The fused row no source-affinity boost may lift another row above (ADR-0005 amendment): the row
///     both legs ranked first, or the fused #1 when it is the keyword top and matched every query term.
/// </summary>
internal static class FusionLeader
{
    public static string? Of(
        IReadOnlyList<MemorySearchResult> fused,
        IReadOnlyList<MemorySearchResult> fts,
        IReadOnlyList<MemorySearchResult> vector,
        IReadOnlySet<string> allTermsMatched)
    {
        if (fts is not [var ftsTop, ..])
        {
            return null;
        }

        if (vector is [var vectorTop, ..] && string.Equals(ftsTop.Hash, vectorTop.Hash, StringComparison.Ordinal))
        {
            return ftsTop.Hash;
        }

        if (fused is [var fusedTop, ..]
            && string.Equals(fusedTop.Hash, ftsTop.Hash, StringComparison.Ordinal)
            && allTermsMatched.Contains(ftsTop.Hash))
        {
            return ftsTop.Hash;
        }

        return null;
    }
}
