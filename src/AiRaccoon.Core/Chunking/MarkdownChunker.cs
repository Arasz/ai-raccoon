using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Chunking;

/// <summary>
///     Line-granular markdown splitter: deterministic, token-bounded and code-fence-aware.
///     No emitted chunk can exceed maxTokens under the tokenizer that counted it (docs/adr/0036):
///     a closed fence stays one atomic unit only while it fits; any unit that is still oversized —
///     a fence, a long line, a minified-JSON line, one very long word — falls back to token-level
///     splitting, which always terminates. Joined multi-unit chunks are verified against the real
///     tokenizer rather than trusted from summed per-unit counts, because BPE/WordPiece token
///     counts are not composable across a join.
/// </summary>
public sealed class MarkdownChunker : IMarkdownChunker
{
    private readonly TokenCount _countTokens;

    public MarkdownChunker(TokenCount countTokens)
    {
        Guard.IsNotNull(countTokens);
        _countTokens = countTokens;
    }

    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) =>
        Split(text, maxTokens, overlayTokens, countTokens ?? _countTokens);

    private static IReadOnlyList<string> Split(string text, int maxTokens, int overlayTokens, TokenCount countTokens)
    {
        Guard.IsNotNull(text);
        Guard.IsGreaterThan(maxTokens, 0);
        Guard.IsGreaterThanOrEqualTo(overlayTokens, 0);
        Guard.IsLessThan(overlayTokens, maxTokens);

        var units = BuildUnits(SplitLines(NormalizeLineEndings(text)), countTokens, maxTokens);
        List<string> chunks = [];
        List<Unit>? previousUnits = null;
        var cursor = 0;
        while (cursor < units.Count)
        {
            var overlay = BuildOverlay(previousUnits, overlayTokens);
            var (chunkUnits, nextCursor) = BuildChunk(units, cursor, overlay, maxTokens, countTokens);
            chunks.Add(string.Concat(chunkUnits.SelectMany(unit => unit.Lines)));
            previousUnits = chunkUnits;
            cursor = nextCursor;
        }

        return chunks;
    }

    /// <summary>
    ///     Greedily packs units (starting at cursor) onto the overlay by summed token count — a fast
    ///     heuristic — then verifies the actual joined text against the real tokenizer. If it drifted
    ///     over budget, the overlay is shed first (it is a nicety, not essential), then trailing new
    ///     units are shed. At least one new unit always survives: every unit was already proven to fit
    ///     maxTokens alone when it was built, so shrinking down to it always terminates and, per that
    ///     same proof, always ends up within budget (docs/adr/0036).
    /// </summary>
    private static (List<Unit> ChunkUnits, int NextCursor) BuildChunk(List<Unit> units, int cursor,
        List<Unit> overlay, int maxTokens, TokenCount countTokens)
    {
        var chunkUnits = new List<Unit>(overlay);
        var tokens = chunkUnits.Sum(unit => unit.TokenCount);
        var newUnitCount = 0;
        var c = cursor;
        while (c < units.Count)
        {
            var next = units[c];
            if (newUnitCount > 0 && tokens + next.TokenCount > maxTokens)
            {
                break;
            }

            chunkUnits.Add(next);
            tokens += next.TokenCount;
            newUnitCount++;
            c++;
        }

        while (countTokens(string.Concat(chunkUnits.SelectMany(unit => unit.Lines))) > maxTokens)
        {
            if (chunkUnits.Count > newUnitCount)
            {
                // Shed the oldest overlay unit first — the overlay is optional context, not content.
                chunkUnits.RemoveAt(0);
                continue;
            }

            if (newUnitCount <= 1)
            {
                // Only the sole new unit is left; it was already proven to fit maxTokens alone.
                break;
            }

            chunkUnits.RemoveAt(chunkUnits.Count - 1);
            newUnitCount--;
            c--;
        }

        return (chunkUnits, c);
    }

    private static List<Unit> BuildOverlay(List<Unit>? previousUnits, int overlayTokens)
    {
        List<Unit> overlay = [];
        if (previousUnits is null || overlayTokens <= 0)
        {
            return overlay;
        }

        var used = 0;
        for (var j = previousUnits.Count - 1; j >= 0; j--)
        {
            var unit = previousUnits[j];
            if (used + unit.TokenCount > overlayTokens)
            {
                break;
            }

            overlay.Insert(0, unit);
            used += unit.TokenCount;
        }

        return overlay;
    }

    /// <summary>
    ///     Groups lines into units, keeping a closed fence atomic only while it fits maxTokens.
    ///     An oversized fence (open-but-never-closed, or closed but over budget) falls back to one
    ///     unit per line for that region instead of gluing it into a single unbounded unit — see
    ///     docs/adr/0036.
    /// </summary>
    private static List<Unit> BuildUnits(List<string> lines, TokenCount countTokens, int maxTokens)
    {
        List<Unit> units = [];
        List<string>? fenceLines = null;
        var fenceTokens = 0;
        foreach (var line in lines)
        {
            if (fenceLines is null)
            {
                if (IsFenceDelimiter(line))
                {
                    fenceLines = [line];
                    fenceTokens = countTokens(line);
                }
                else
                {
                    AddUnitOrSplit(units, line, maxTokens, countTokens);
                }

                continue;
            }

            fenceLines.Add(line);
            fenceTokens += countTokens(line);
            if (IsFenceDelimiter(line))
            {
                FlushFence(units, fenceLines, fenceTokens, maxTokens, countTokens);
                fenceLines = null;
                fenceTokens = 0;
            }
            else if (fenceTokens > maxTokens)
            {
                // Already broken; keeping it atomic buys nothing, so stop treating it as fenced.
                FlushAsLines(units, fenceLines, maxTokens, countTokens);
                fenceLines = null;
                fenceTokens = 0;
            }
        }

        if (fenceLines is not null)
        {
            // Never closed: not a well-formed fence, so it must not glue the rest of the note together.
            FlushAsLines(units, fenceLines, maxTokens, countTokens);
        }

        return units;
    }

    private static void FlushFence(List<Unit> units, List<string> fenceLines, int fenceTokens, int maxTokens,
        TokenCount countTokens)
    {
        // fenceTokens is a summed estimate (cheap, used only to decide atomic-vs-fallback); the
        // atomic unit itself is recounted exactly against the real joined text so it carries an
        // exact count, not an estimate that could drift under a non-composable tokenizer.
        if (fenceTokens <= maxTokens)
        {
            var exact = countTokens(string.Concat(fenceLines));
            if (exact <= maxTokens)
            {
                units.Add(new Unit(fenceLines, exact));
                return;
            }
        }

        FlushAsLines(units, fenceLines, maxTokens, countTokens);
    }

    private static void FlushAsLines(List<Unit> units, List<string> lines, int maxTokens, TokenCount countTokens)
    {
        foreach (var line in lines)
        {
            AddUnitOrSplit(units, line, maxTokens, countTokens);
        }
    }

    /// <summary>
    ///     Adds text as one unit when it already fits maxTokens; otherwise falls back to
    ///     token-level splitting via <see cref="LargestPrefixWithinBudget" />, which always makes
    ///     progress. This is the floor beneath every coarser split (fence, line): no unit this
    ///     builds can ever exceed maxTokens, whatever it contains — a long line, a minified-JSON
    ///     blob, one very long word (docs/adr/0036).
    /// </summary>
    private static void AddUnitOrSplit(List<Unit> units, string text, int maxTokens, TokenCount countTokens)
    {
        var count = countTokens(text);
        if (count <= maxTokens)
        {
            units.Add(new Unit([text], count));
            return;
        }

        var remaining = text;
        while (remaining.Length > 0)
        {
            var headLength = LargestPrefixWithinBudget(remaining, maxTokens, countTokens);
            if (headLength <= 0)
            {
                // Even a single character tokenizes over budget; take it anyway so every split makes
                // forward progress and the loop is guaranteed to terminate.
                headLength = 1;
            }

            var head = remaining[..headLength];
            units.Add(new Unit([head], countTokens(head)));
            remaining = remaining[headLength..];
        }
    }

    /// <summary>Binary search (assumes token count is non-decreasing in prefix length, true of every
    /// tokenizer this project uses) for the longest prefix of text within the token budget.</summary>
    private static int LargestPrefixWithinBudget(string text, int maxTokens, TokenCount countTokens)
    {
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) / 2);
            if (countTokens(text[..mid]) <= maxTokens)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private static bool IsFenceDelimiter(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal)
               || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static List<string> SplitLines(string text)
    {
        List<string> lines = [];
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record Unit(List<string> Lines, int TokenCount);
}
