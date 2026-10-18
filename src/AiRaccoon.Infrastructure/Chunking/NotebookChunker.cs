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
            builder.Append("```").Append(language).Append('\n').Append(EscapeFenceLines(source)).Append("\n```\n\n");
        }
        else
        {
            builder.Append(DemoteHeadings(source)).Append("\n\n");
        }
    }

    // The markdown chunker toggles a fence on any line opening with ``` or ~~~, so such a line in a code
    // cell would end the cell's fence early; a leading backslash keeps it literal.
    private static string EscapeFenceLines(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
            IsFenceLine(line) ? line.Insert(line.Length - line.TrimStart().Length, "\\") : line));

    private static bool IsFenceLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Pushes ATX headings in a markdown cell two levels down (capped at six) so they nest under the
    ///     cell's own heading; lines inside fenced code (by the markdown chunker's toggle rule) and setext headings stay as written.
    /// </summary>
    private static string DemoteHeadings(string source)
    {
        var lines = source.Split('\n');
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsFenceLine(lines[i]))
            {
                inFence = !inFence;
                continue;
            }

            var trimmed = lines[i].TrimStart(' ');
            if (!inFence && IsAtxHeading(trimmed, out var level))
            {
                lines[i] = new string('#', Math.Min(6, level + 2)) + trimmed[level..];
            }
        }

        // A fence may run to the end of its cell; closing it keeps it from swallowing the next cell.
        return inFence ? string.Join('\n', lines) + "\n```" : string.Join('\n', lines);
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
