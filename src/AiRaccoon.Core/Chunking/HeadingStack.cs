namespace AiRaccoon.Core.Chunking;

/// <summary>
///     The heading-stack rules shared by <see cref="HeadingPathParser" /> and
///     <see cref="MarkdownChunker" />'s per-unit context builder: a deeper-or-equal level pops
///     before the new one pushes, level 1 is truncated to its identifier segment, and the joined
///     path always reflects the current stack (docs/adr/0004).
/// </summary>
internal sealed class HeadingStack
{
    private readonly List<(int Level, string Text)> _stack = [];

    public string Path { get; private set; } = "";

    /// <summary>The innermost heading on the stack — what a `file#section` anchor names; "" when empty.</summary>
    public string Leaf => _stack.Count == 0 ? "" : _stack[^1].Text;

    /// <param name="level">1 or 2 — the caller decides whether a heading counts as a section opener.</param>
    public void Push(int level, string text)
    {
        if (level == 1)
        {
            text = IdentifierSegment(text);
        }

        while (_stack.Count > 0 && _stack[^1].Level >= level)
        {
            _stack.RemoveAt(_stack.Count - 1);
        }

        _stack.Add((level, text));
        Path = string.Join(" > ", _stack.Select(h => h.Text));
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
