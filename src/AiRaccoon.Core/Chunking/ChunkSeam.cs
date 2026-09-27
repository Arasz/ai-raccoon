namespace AiRaccoon.Core.Chunking;

/// <summary>Whether two stored, adjacent chunks were cut through the middle of a search term. Pure.</summary>
public static class ChunkSeam
{
    /// <summary>The first chunk ends and the second begins inside one term: the old hard cut on a long line.</summary>
    public static bool CutsATerm(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return before.Length > 0 && after.Length > 0 && TokenBudget.IsTermChar(before[^1]) && TokenBudget.IsTermChar(after[0]);
    }

    /// <summary>
    ///     The same cut inside a re-fenced code block: the first chunk's content ends in a term just before its
    ///     closing fence, and the second's starts with one just after its opener. The line break before that
    ///     closer may be real, so only the source file can confirm it.
    /// </summary>
    public static bool MayCutAFencedTerm(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (before.Length < 3 || !before.EndsWith('\n'))
        {
            return false;
        }

        var closerStart = before.LastIndexOf('\n', before.Length - 2) + 1;
        if (closerStart < 2 || !IsFenceLine(before[closerStart..]) || !TokenBudget.IsTermChar(before[closerStart - 2]))
        {
            return false;
        }

        var openerEnd = after.IndexOf('\n');
        return openerEnd > 0 && openerEnd + 1 < after.Length && IsFenceLine(after[..openerEnd])
               && TokenBudget.IsTermChar(after[openerEnd + 1]);
    }

    private static bool IsFenceLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }
}
