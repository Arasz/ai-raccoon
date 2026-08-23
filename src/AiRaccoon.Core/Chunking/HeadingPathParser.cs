namespace AiRaccoon.Core.Chunking;

/// <summary>
///     Heading-path context of a markdown chunk (e.g. "ADR-0011 > Decision"); "" when the
///     chunk has no headings. Path rules per docs/adr/0004.
/// </summary>
public static class HeadingPathParser
{
    public static string Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var stack = new HeadingStack();
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
            // (docs/adr/0004-dual-vector-structure-signal.md).
            if (level > 2)
            {
                continue;
            }

            stack.Push(level, text);
        }

        return stack.Path;
    }

    /// <summary>The leaf of a " > "-joined heading path — what a `file#section` anchor names; "" when empty.
    /// The bare '>' is not a separator: a heading may carry one of its own ("<!-- REQUIRED -->").</summary>
    public static string Leaf(string headingPath)
    {
        var lastSeparator = headingPath.LastIndexOf(" > ", StringComparison.Ordinal);
        return (lastSeparator < 0 ? headingPath : headingPath[(lastSeparator + 3)..]).Trim();
    }
}
