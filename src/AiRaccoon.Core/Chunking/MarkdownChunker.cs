using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Chunking;

/// <summary>
///     Line-granular markdown splitter: deterministic, token-bounded and code-fence-aware.
///     Fences (``` and ~~~) are atomic units — a boundary never falls inside one, even past maxTokens.
/// </summary>
public static class MarkdownChunker
{
    public static IReadOnlyList<string> Split(string text, int maxTokens, int overlayTokens, TokenCount countTokens)
    {
        Guard.IsNotNull(text);
        Guard.IsNotNull(countTokens);
        Guard.IsGreaterThan(maxTokens, 0);
        Guard.IsGreaterThanOrEqualTo(overlayTokens, 0);
        Guard.IsLessThan(overlayTokens, maxTokens);

        var units = BuildUnits(SplitLines(NormalizeLineEndings(text)), countTokens);
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

    private static List<Unit> BuildUnits(List<string> lines, TokenCount countTokens)
    {
        List<Unit> units = [];
        List<string>? fenceLines = null;
        foreach (var line in lines)
        {
            if (IsFenceDelimiter(line))
            {
                if (fenceLines is null)
                {
                    fenceLines = [line];
                }
                else
                {
                    fenceLines.Add(line);
                    units.Add(new Unit(fenceLines, countTokens(string.Concat(fenceLines))));
                    fenceLines = null;
                }
            }
            else if (fenceLines is not null)
            {
                fenceLines.Add(line);
            }
            else
            {
                units.Add(new Unit([line], countTokens(line)));
            }
        }

        if (fenceLines is not null)
        {
            units.Add(new Unit(fenceLines, countTokens(string.Concat(fenceLines))));
        }

        return units;
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
