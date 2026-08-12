using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
/// File type handler for markdown and plain text files (.md, .markdown, .txt).
/// </summary>
public sealed class MarkdownFileTypeHandler : IFileTypeHandler
{
    public string Name => "Markdown";

    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt"
    };

    public IChunker Chunker { get; }

    public MarkdownFileTypeHandler(IChunker? chunker = null)
    {
        Chunker = chunker ?? new TokenizerChunker();
    }
}
