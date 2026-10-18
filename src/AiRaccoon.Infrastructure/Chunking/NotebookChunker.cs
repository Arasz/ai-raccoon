using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Chunking;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>
///     Flattens a notebook's markdown and code cells into markdown, one heading per cell, and hands
///     the result to the markdown chunker. Outputs, attachments and metadata are dropped; unreadable input yields no chunks.
/// </summary>
public sealed class NotebookChunker : INotebookChunker
{
    private readonly IMarkdownChunker _markdownChunker;

    public NotebookChunker(IMarkdownChunker markdownChunker)
    {
        Guard.IsNotNull(markdownChunker);
        _markdownChunker = markdownChunker;
    }

    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null)
    {
        var markdown = Flatten(text);
        return markdown.Length == 0
            ? []
            : _markdownChunker.Chunk(markdown, maxTokens, overlayTokens, countTokens).Where(HasContent).ToList();
    }

    public IReadOnlyList<TextChunk> ChunkWithHeadings(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null)
    {
        var markdown = Flatten(text);
        return markdown.Length == 0
            ? []
            : _markdownChunker.ChunkWithHeadings(markdown, maxTokens, overlayTokens, countTokens)
                .Where(chunk => HasContent(chunk.Text)).ToList();
    }

    // The markdown chunker can emit a chunk holding only a cell heading when a cell's fence is split.
    private static bool HasContent(string chunk) =>
        chunk.Split('\n').Any(line => line.Trim() is { Length: > 0 } trimmed && !trimmed.StartsWith("## cell ", StringComparison.Ordinal));

    private static string Flatten(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cells", out var cells)
                || cells.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var language = Language(root);
            var builder = new StringBuilder();
            var index = 0;
            foreach (var cell in cells.EnumerateArray())
            {
                index++;
                AppendCell(builder, cell, index, language);
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static void AppendCell(StringBuilder builder, JsonElement cell, int index, string language)
    {
        if (cell.ValueKind != JsonValueKind.Object || !cell.TryGetProperty("cell_type", out var typeElement))
        {
            return;
        }

        var cellType = typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
        if (cellType is not ("code" or "markdown"))
        {
            return;
        }

        var source = Source(cell).Trim('\r', '\n');
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        builder.Append("## cell ").Append(index).Append(" (").Append(cellType).Append(")\n\n");
        if (cellType == "code")
        {
            var fence = new string('`', FenceLength(source));
            builder.Append(fence).Append(language).Append('\n').Append(source).Append('\n').Append(fence).Append("\n\n");
        }
        else
        {
            builder.Append(DemoteHeadings(source)).Append("\n\n");
        }
    }

    /// <summary>A fence longer than any backtick run opening a line of <paramref name="source" />, minimum three.</summary>
    private static int FenceLength(string source)
    {
        var longest = 0;
        foreach (var line in source.Split('\n'))
        {
            var run = 0;
            while (run < line.Length && line[run] == '`')
            {
                run++;
            }

            longest = Math.Max(longest, run);
        }

        return Math.Max(3, longest + 1);
    }

    /// <summary>
    ///     Pushes ATX headings in a markdown cell two levels down (capped at six) so they nest under the
    ///     cell's own heading; lines inside fenced code and setext headings are left as written.
    /// </summary>
    private static string DemoteHeadings(string source)
    {
        var lines = source.Split('\n');
        char? fence = null;
        var fenceLength = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart(' ');
            var marker = trimmed.Length > 0 && trimmed[0] is '`' or '~' ? trimmed[0] : (char?)null;
            if (marker is { } m)
            {
                var run = trimmed.TakeWhile(c => c == m).Count();
                if (run >= 3 && fence is null)
                {
                    fence = m;
                    fenceLength = run;
                    continue;
                }

                if (run >= fenceLength && fence == m && trimmed[run..].Trim().Length == 0)
                {
                    fence = null;
                    continue;
                }
            }

            if (fence is null && IsAtxHeading(trimmed, out var level))
            {
                lines[i] = new string('#', Math.Min(6, level + 2)) + trimmed[level..];
            }
        }

        return string.Join('\n', lines);
    }

    private static bool IsAtxHeading(string line, out int level)
    {
        level = line.TakeWhile(c => c == '#').Count();
        return level is >= 1 and <= 6 && (line.Length == level || line[level] is ' ' or '\t' or '\r');
    }

    private static string Source(JsonElement cell)
    {
        if (!cell.TryGetProperty("source", out var source))
        {
            return string.Empty;
        }

        switch (source.ValueKind)
        {
            case JsonValueKind.String:
                return source.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (var line in source.EnumerateArray())
                {
                    if (line.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(line.GetString());
                    }
                }

                return builder.ToString();
            default:
                return string.Empty;
        }
    }

    private static string Language(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return NamedString(metadata, "kernelspec", "language") is { Length: > 0 } kernel
            ? kernel
            : NamedString(metadata, "language_info", "name");
    }

    private static string NamedString(JsonElement metadata, string section, string key) =>
        metadata.TryGetProperty(section, out var node) && node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
