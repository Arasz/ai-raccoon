namespace AiRaccoon.Core.Chunking;

/// <summary>
///     Extracts the heading-path context a markdown chunk belongs to, e.g.
///     "ADR-0011 > Decision"; "" when the chunk has no headings. The path is the heading
///     stack at the chunk's last section heading (H1/H2): the H1 contributes only its
///     identifier segment (text before ':' / '—' / '|') and sub-sections (H3+) are dropped —
///     both measured requirements for the identifier signal to survive embedding (plan C
///     Wave 6; docs/adr/0004).
/// </summary>
public static class HeadingPathParser
{
    public static string Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var stack = new List<(int Level, string Text)>();
        var path = "";
        var inFence = false;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimStart();

            // Fences swallow everything up to the closing marker, so heading lookalikes
            // inside code blocks never enter the stack.
            if (line.StartsWith("```", StringComparison.Ordinal)
                || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || !line.StartsWith('#'))
            {
                continue;
            }

            // Ingest provenance header ('## Source: <structured path>') is metadata, not content.
            if (line.StartsWith("## Source:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var level = 0;
            while (level < line.Length && line[level] == '#')
            {
                level++;
            }

            // Markdown has six heading levels; '#' alone or '#Text' is not a heading.
            if (level is 0 or > 6 || level == line.Length || line[level] != ' ')
            {
                continue;
            }

            var text = line[level..].Trim();
            if (text.Length == 0)
            {
                continue;
            }

            // Sub-sections do not extend the path: they dilute the identifier signal
            // (measured: S2 structure sim 0.88 without vs 0.34 with the H3 tail).
            if (level > 2)
            {
                continue;
            }

            if (level == 1)
            {
                text = IdentifierSegment(text);
            }

            while (stack.Count > 0 && stack[^1].Level >= level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack.Add((level, text));
            path = string.Join(" > ", stack.Select(h => h.Text));
        }

        return path;
    }

    private static string IdentifierSegment(string headingText)
    {
        foreach (var separator in new[] { ':', '—', '|' })
        {
            var index = headingText.IndexOf(separator);
            if (index > 0)
            {
                return headingText[..index].Trim();
            }
        }

        return headingText;
    }
}
