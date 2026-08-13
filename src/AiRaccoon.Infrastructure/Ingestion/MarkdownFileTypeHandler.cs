using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     File type handler for Markdown and plain text files (.md, .markdown, .txt).
/// </summary>
public sealed class MarkdownFileTypeHandler(IMarkdownChunker chunker) : IFileTypeHandler
{
    public string Name => "Markdown";

    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt"
    };

    public IChunker Chunker { get; } = chunker;
}
