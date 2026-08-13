using AiRaccoon.Core.Chunking;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     File type handler for Markdown and plain text files (.md, .markdown, .txt).
/// </summary>
public sealed class MarkdownFileTypeHandler(IChunker chunker) : IFileTypeHandler
{
    public string Name => "Markdown";

    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt"
    };

    public IChunker Chunker { get; } = chunker;
}
