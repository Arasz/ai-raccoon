using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Chunking;

/// <summary>
///     Line-granular markdown splitter: deterministic, token-bounded and code-fence-aware.
///     No emitted chunk can exceed maxTokens under the tokenizer that counted it (docs/adr/0036):
///     a closed fence stays one atomic unit only while it fits; any unit that is still oversized —
///     a fence, a long line, a minified-JSON line, one very long word — falls back to token-level
///     splitting, which always terminates. Joined multi-unit chunks are verified against the real
///     tokenizer rather than trusted from summed per-unit counts, because BPE/WordPiece token
///     counts are not composable across a join. Every chunk is also a well-formed markdown
///     fragment: a boundary never falls inside a fence, because an over-budget fence is re-fenced
///     into several bounded fences rather than de-fenced into bare lines (docs/adr/0048).
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

        int deferred;
        do
        {
            deferred = DeferOpenSection(chunkUnits, newUnitCount, units, c);
            newUnitCount -= deferred;
            c -= deferred;
        } while (deferred > 0);

        return (chunkUnits, c);
    }

    /// <summary>
    ///     A heading line must open the chunk that holds its section's content, not end the previous
    ///     one empty-handed (#489, generalized by #538): the LAST heading among this chunk's new
    ///     units opens a section that is cut by the chunk boundary whenever the next unit after the
    ///     chunk exists and is not itself a heading — that heading and everything after it are handed
    ///     back to the next chunk, whether "everything after it" is a few body lines (#538) or nothing
    ///     but blank lines (#489's own case). The heading is never deferred when it is the chunk's
    ///     first new unit; deferring it then would make no forward progress and would defer forever a
    ///     section too long to fit in any one chunk.
    /// </summary>
    private static int DeferOpenSection(List<Unit> chunkUnits, int newUnitCount, List<Unit> units, int cursor)
    {
        var newStart = chunkUnits.Count - newUnitCount;
        var i = -1;
        for (var idx = chunkUnits.Count - 1; idx > newStart; idx--)
        {
            if (IsHeadingUnit(chunkUnits[idx]))
            {
                i = idx;
                break;
            }
        }

        if (i < 0)
        {
            return 0;
        }

        if (cursor >= units.Count || IsHeadingUnit(units[cursor]))
        {
            return 0;
        }

        var removeCount = chunkUnits.Count - i;
        if (newUnitCount - removeCount < 1)
        {
            return 0;
        }

        chunkUnits.RemoveRange(i, removeCount);
        return removeCount;
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
    ///     Groups lines into units, keeping a closed fence atomic only while it fits maxTokens. An
    ///     oversized fence is re-fenced into several bounded fences rather than de-fenced into bare
    ///     lines, and a never-closed one is closed first, so every unit is a well-formed fence
    ///     (docs/adr/0048) and still bounded by maxTokens (docs/adr/0036).
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
        }

        if (fenceLines is not null)
        {
            // Never closed: close it here rather than de-fence it. Abandoning the fence mid-region
            // is what let a boundary land inside a code block, and it also inverted the fence
            // parity of everything after the region's real closing delimiter.
            var closer = CloserFor(fenceLines[0]);
            fenceLines[^1] = EndWithNewline(fenceLines[^1]);
            fenceLines.Add(closer);
            FlushFence(units, fenceLines, fenceTokens + countTokens(closer), maxTokens, countTokens);
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

        FlushAsSubFences(units, fenceLines, maxTokens, countTokens);
    }

    /// <summary>
    ///     Splits an over-budget fence into consecutive bounded fences, each repeating the region's
    ///     own opening and closing delimiter. Every unit stays a well-formed fence, so no chunk can
    ///     begin inside a code block and be read as prose (docs/adr/0048). Falls back to bare lines
    ///     only when the delimiters alone leave no room for content — the token budget outranks the
    ///     balance, and that floor is unreachable at any realistic maxTokens.
    /// </summary>
    private static void FlushAsSubFences(List<Unit> units, List<string> fenceLines, int maxTokens,
        TokenCount countTokens)
    {
        var opener = EndWithNewline(fenceLines[0]);
        var closer = EndWithNewline(fenceLines[^1]);
        if (countTokens(string.Concat(opener, closer)) >= maxTokens)
        {
            FlushAsLines(units, fenceLines, maxTokens, countTokens);
            return;
        }

        List<string> pieces = [];
        for (var i = 1; i < fenceLines.Count - 1; i++)
        {
            pieces.AddRange(SplitForSubFence(fenceLines[i], opener, closer, maxTokens, countTokens));
        }

        List<string> current = [];
        foreach (var piece in pieces)
        {
            current.Add(piece);

            // Every piece was sized against the delimiters it will be emitted with, so a single one
            // always fits and shedding down to it terminates.
            if (current.Count == 1 || countTokens(SubFenceText(opener, current, closer)) <= maxTokens)
            {
                continue;
            }

            current.RemoveAt(current.Count - 1);
            AddSubFence(units, opener, current, closer, countTokens);
            current = [piece];
        }

        AddSubFence(units, opener, current, closer, countTokens);
    }

    private static void AddSubFence(List<Unit> units, string opener, List<string> content, string closer,
        TokenCount countTokens)
    {
        var lines = SubFenceLines(opener, content, closer);
        units.Add(new Unit(lines, countTokens(string.Concat(lines))));
    }

    /// <summary>Splits one fenced line into pieces that each fit maxTokens once wrapped in the delimiters.</summary>
    private static List<string> SplitForSubFence(string line, string opener, string closer, int maxTokens,
        TokenCount countTokens)
    {
        int Wrapped(string text) => countTokens(SubFenceText(opener, [text], closer));

        List<string> pieces = [];
        var remaining = line;
        while (remaining.Length > 0)
        {
            if (Wrapped(remaining) <= maxTokens)
            {
                pieces.Add(remaining);
                break;
            }

            // Even a single character can tokenize over budget; take it anyway so every split makes
            // forward progress and the loop is guaranteed to terminate.
            var headLength = Math.Max(1, LargestPrefixWithinBudget(remaining, maxTokens, Wrapped));
            pieces.Add(remaining[..headLength]);
            remaining = remaining[headLength..];
        }

        return pieces;
    }

    /// <summary>A mid-line split leaves the last piece without its newline; the closing delimiter still needs its own line.</summary>
    private static List<string> SubFenceLines(string opener, IReadOnlyList<string> content, string closer)
    {
        List<string> lines = [opener, .. content, closer];
        lines[^2] = EndWithNewline(lines[^2]);
        return lines;
    }

    private static string SubFenceText(string opener, IReadOnlyList<string> content, string closer) =>
        string.Concat(SubFenceLines(opener, content, closer));

    /// <summary>The closing delimiter matching an opener's marker character and run length.</summary>
    private static string CloserFor(string openerLine)
    {
        var trimmed = openerLine.TrimStart();
        var marker = trimmed[0];
        var run = 0;
        while (run < trimmed.Length && trimmed[run] == marker)
        {
            run++;
        }

        return new string(marker, run) + "\n";
    }

    private static string EndWithNewline(string text) => text.EndsWith('\n') ? text : text + "\n";

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

    private static bool IsHeadingUnit(Unit unit) => unit.Lines.Count == 1 && IsHeadingLine(unit.Lines[0]);

    /// <summary>ATX heading, levels 1-6 (a bare '#' or '#Text' does not count) — same shape HeadingPathParser parses.</summary>
    private static bool IsHeadingLine(string line)
    {
        var trimmed = line.TrimStart();
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' '
               && trimmed[(level + 1)..].Trim().Length > 0;
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
