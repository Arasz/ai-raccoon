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

    /// <summary>
    ///     Same units, boundaries and budgets as <see cref="Chunk" /> (docs/adr/0036 untouched) —
    ///     the only differences are the reported <see cref="TextChunk.HeadingPath" /> (the context
    ///     already known from a single forward pass, docs/adr/0048 #549/#550 amendment, instead of
    ///     re-parsing each chunk's own text) and that a whitespace-only chunk is never emitted
    ///     (Rule D, #550-3) — only whitespace is ever dropped.
    /// </summary>
    public IReadOnlyList<TextChunk> ChunkWithHeadings(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) =>
        SplitWithHeadings(text, maxTokens, overlayTokens, countTokens ?? _countTokens);

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
            var (chunkUnits, nextCursor, _) = BuildChunk(units, cursor, overlay, maxTokens, countTokens);
            chunks.Add(string.Concat(chunkUnits.SelectMany(unit => unit.Lines)));
            previousUnits = chunkUnits;
            cursor = nextCursor;
        }

        return chunks;
    }

    private static IReadOnlyList<TextChunk> SplitWithHeadings(string text, int maxTokens, int overlayTokens, TokenCount countTokens)
    {
        Guard.IsNotNull(text);
        Guard.IsGreaterThan(maxTokens, 0);
        Guard.IsGreaterThanOrEqualTo(overlayTokens, 0);
        Guard.IsLessThan(overlayTokens, maxTokens);

        var units = BuildUnits(SplitLines(NormalizeLineEndings(text)), countTokens, maxTokens);
        var contexts = BuildContexts(units);
        List<TextChunk> chunks = [];
        List<Unit>? previousUnits = null;
        var cursor = 0;
        while (cursor < units.Count)
        {
            var overlay = BuildOverlay(previousUnits, overlayTokens);
            var (chunkUnits, nextCursor, newUnitCount) = BuildChunk(units, cursor, overlay, maxTokens, countTokens);
            var chunkText = string.Concat(chunkUnits.SelectMany(unit => unit.Lines));
            if (!string.IsNullOrWhiteSpace(chunkText))
            {
                chunks.Add(new TextChunk(chunkText, HeadingPathFor(chunkUnits, newUnitCount, cursor, contexts),
                    SectionsFor(chunkUnits, newUnitCount, cursor, contexts)));
            }

            previousUnits = chunkUnits;
            cursor = nextCursor;
        }

        return chunks;
    }

    /// <summary>
    ///     One forward pass over every unit in the document, recording the heading path in force
    ///     BEFORE each one (the same stack HeadingPathParser walks, docs/adr/0004) — independent of
    ///     where chunk boundaries later fall, so a continuation chunk or a fence pushed out by an
    ///     oversized next unit still knows its section (#549).
    /// </summary>
    private static List<string> BuildContexts(List<Unit> units)
    {
        var contexts = new List<string>(units.Count);
        var stack = new HeadingStack();
        foreach (var unit in units)
        {
            contexts.Add(stack.Path);
            if (IsSectionOpenerUnit(unit))
            {
                var (level, opener) = SectionOpenerLevelAndText(unit);
                stack.Push(level, opener);
            }
        }

        return contexts;
    }

    /// <summary>A chunk's path is the context of its FIRST contentful new unit (#549/#550: stated
    /// positively — a chunk that only opens a section claims none); overlay units are never eligible.</summary>
    private static string HeadingPathFor(List<Unit> chunkUnits, int newUnitCount, int cursor, List<string> contexts)
    {
        var newStart = chunkUnits.Count - newUnitCount;
        for (var idx = newStart; idx < chunkUnits.Count; idx++)
        {
            if (IsContentfulUnit(chunkUnits[idx]))
            {
                return contexts[cursor + (idx - newStart)];
            }
        }

        return "";
    }

    /// <summary>The leaf of every section the chunk holds content for, in order — one chunk may
    /// hold several whole sections, and each one's `file#section` anchor must resolve (#549).</summary>
    private static IReadOnlyList<string> SectionsFor(List<Unit> chunkUnits, int newUnitCount, int cursor, List<string> contexts)
    {
        var newStart = chunkUnits.Count - newUnitCount;
        List<string> sections = [];
        for (var idx = newStart; idx < chunkUnits.Count; idx++)
        {
            if (!IsContentfulUnit(chunkUnits[idx]))
            {
                continue;
            }

            var leaf = HeadingPathParser.Leaf(contexts[cursor + (idx - newStart)]);
            if (leaf.Length > 0 && (sections.Count == 0 || sections[^1] != leaf))
            {
                sections.Add(leaf);
            }
        }

        return sections;
    }

    /// <summary>
    ///     Greedily packs units (starting at cursor) onto the overlay by summed token count — a fast
    ///     heuristic — then verifies the actual joined text against the real tokenizer. If it drifted
    ///     over budget, the overlay is shed first (it is a nicety, not essential), then trailing new
    ///     units are shed. At least one new unit always survives: every unit was already proven to fit
    ///     maxTokens alone when it was built, so shrinking down to it always terminates and, per that
    ///     same proof, always ends up within budget (docs/adr/0036).
    /// </summary>
    private static (List<Unit> ChunkUnits, int NextCursor, int NewUnitCount) BuildChunk(List<Unit> units, int cursor,
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

        return (chunkUnits, c, newUnitCount);
    }

    /// <summary>A heading opens the chunk holding its section; a cut section defers whole
    /// (docs/adr/0048, #538 amendment).</summary>
    private static int DeferOpenSection(List<Unit> chunkUnits, int newUnitCount, List<Unit> units, int cursor)
    {
        var newStart = chunkUnits.Count - newUnitCount;
        var i = -1;
        for (var idx = chunkUnits.Count - 1; idx > newStart; idx--)
        {
            if (IsSectionOpenerUnit(chunkUnits[idx]))
            {
                i = idx;
                break;
            }
        }

        if (i < 0)
        {
            return 0;
        }

        var headingHasOwnContent = false;
        for (var idx = i + 1; idx < chunkUnits.Count; idx++)
        {
            if (!IsBlankUnit(chunkUnits[idx]))
            {
                headingHasOwnContent = true;
                break;
            }
        }

        var sectionIsCut = !headingHasOwnContent || SectionContinuesPastChunk(units, cursor);
        if (!sectionIsCut)
        {
            return 0;
        }

        // A deferral must leave at least one contentful new unit behind, or the chunk collapses to
        // whitespace or a lone provenance header (neither is indexable) — refuse it instead.
        var remainingHasContent = false;
        for (var idx = newStart; idx < i; idx++)
        {
            if (IsContentfulUnit(chunkUnits[idx]))
            {
                remainingHasContent = true;
                break;
            }
        }

        if (!remainingHasContent)
        {
            return 0;
        }

        var removeCount = chunkUnits.Count - i;
        chunkUnits.RemoveRange(i, removeCount);
        return removeCount;
    }

    /// <summary>Skips blank-only units past the chunk boundary before asking whether real content
    /// follows: only a real, non-opener unit proves the section continues past the boundary.</summary>
    private static bool SectionContinuesPastChunk(List<Unit> units, int cursor)
    {
        var idx = cursor;
        while (idx < units.Count && IsBlankUnit(units[idx]))
        {
            idx++;
        }

        return idx < units.Count && !IsSectionOpenerUnit(units[idx]);
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
    ///     only when even an empty sub-fence (opener + the forced content newline + closer) would
    ///     not fit — the token budget outranks the balance, and that floor is unreachable at any
    ///     realistic maxTokens.
    /// </summary>
    private static void FlushAsSubFences(List<Unit> units, List<string> fenceLines, int maxTokens,
        TokenCount countTokens)
    {
        var opener = EndWithNewline(fenceLines[0]);
        var closer = EndWithNewline(fenceLines[^1]);
        if (countTokens(SubFenceText(opener, [""], closer)) >= maxTokens)
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

    /// <summary>The headings DeferOpenSection may cut a section on, and BuildContexts pushes onto
    /// the heading stack: levels 1-2, never the ingest "## Source:" provenance header — the same
    /// headings HeadingPathParser keeps (docs/adr/0004).</summary>
    private static bool IsSectionOpenerUnit(Unit unit) =>
        IsHeadingUnit(unit) && HeadingLevel(unit.Lines[0].TrimStart()) <= 2 && !IsSourceProvenanceUnit(unit);

    /// <summary>The (level, text) HeadingStack.Push needs for a unit already known to be a section
    /// opener — same level scan IsHeadingLine/IsSectionOpenerUnit use, same text HeadingPathParser
    /// would extract.</summary>
    private static (int Level, string Text) SectionOpenerLevelAndText(Unit unit)
    {
        var trimmed = unit.Lines[0].TrimStart();
        var level = HeadingLevel(trimmed);
        return (level, trimmed[level..].Trim());
    }

    private static bool IsSourceProvenanceUnit(Unit unit) =>
        unit.Lines.Count == 1 && unit.Lines[0].TrimStart().StartsWith("## Source:", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlankUnit(Unit unit) => unit.Lines.Count == 1 && string.IsNullOrWhiteSpace(unit.Lines[0]);

    /// <summary>A unit worth leaving behind after a deferral, or worth labelling a chunk by: not
    /// blank and not itself a heading (any level) — a lone heading is exactly as unindexable as a
    /// lone blank line.</summary>
    private static bool IsContentfulUnit(Unit unit) => !IsBlankUnit(unit) && !IsHeadingUnit(unit);

    /// <summary>ATX heading, levels 1-6 (a bare '#' or '#Text' does not count) — same shape HeadingPathParser parses.</summary>
    private static bool IsHeadingLine(string line)
    {
        var trimmed = line.TrimStart();
        var level = HeadingLevel(trimmed);
        return level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' '
               && trimmed[(level + 1)..].Trim().Length > 0;
    }

    /// <summary>Count of leading '#' characters in an already-TrimStart'd line.</summary>
    private static int HeadingLevel(string trimmedLine)
    {
        var level = 0;
        while (level < trimmedLine.Length && trimmedLine[level] == '#')
        {
            level++;
        }

        return level;
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
