namespace AiRaccoon.Core.Chunking;

/// <summary>Line splitting shared by the chunkers: each line keeps its trailing newline, so the lines join back to the input.</summary>
public static class TextLines
{
    /// <summary>Splits on LF, keeping each line's newline; a final line without one is kept as is.</summary>
    public static List<string> Split(string text)
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

    /// <summary>Rewrites CRLF and lone CR endings as LF.</summary>
    public static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
