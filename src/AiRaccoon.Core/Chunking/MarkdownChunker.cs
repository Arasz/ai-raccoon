using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Chunking;

/// <summary>
///     Line-granular markdown splitter: deterministic, token-bounded and code-fence-aware.
///     A closed fence (``` or ~~~) that fits within maxTokens is one atomic unit; an oversized or
///     never-closed fence falls back to line-granular units instead (docs/adr/0036).
/// </summary>
public sealed class MarkdownChunker : IMarkdownChunker
{
    private readonly TokenCount _countTokens;

    public MarkdownChunker(TokenCount countTokens)
    {
        Guard.IsNotNull(countTokens);
        _countTokens = countTokens;
    }

    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
        Split(text, maxTokens, overlayTokens, _countTokens);

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
            var chunkUnits = BuildOverlay(previousUnits, overlayTokens);
            var tokens = chunkUnits.Sum(unit => unit.TokenCount);
            var firstNew = cursor;
            while (cursor < units.Count)
            {
                var next = units[cursor];
                if (cursor != firstNew && tokens + next.TokenCount > maxTokens)
                {
                    break;
                }

                chunkUnits.Add(next);
                tokens += next.TokenCount;
                cursor++;
            }

            chunks.Add(string.Concat(chunkUnits.SelectMany(unit => unit.Lines)));
            previousUnits = chunkUnits;
        }

        return chunks;
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
                    units.Add(new Unit([line], countTokens(line)));
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
                FlushAsLines(units, fenceLines, countTokens);
                fenceLines = null;
                fenceTokens = 0;
            }
        }

        if (fenceLines is not null)
        {
            // Never closed: not a well-formed fence, so it must not glue the rest of the note together.
            FlushAsLines(units, fenceLines, countTokens);
        }

        return units;
    }

    private static void FlushFence(List<Unit> units, List<string> fenceLines, int fenceTokens, int maxTokens,
        TokenCount countTokens)
    {
        if (fenceTokens <= maxTokens)
        {
            units.Add(new Unit(fenceLines, countTokens(string.Concat(fenceLines))));
        }
        else
        {
            FlushAsLines(units, fenceLines, countTokens);
        }
    }

    private static void FlushAsLines(List<Unit> units, List<string> lines, TokenCount countTokens)
    {
        foreach (var line in lines)
        {
            units.Add(new Unit([line], countTokens(line)));
        }
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
