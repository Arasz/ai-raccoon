using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     File type handler for plain text files (.txt).
/// </summary>
public sealed class PlainTextFileTypeHandler(IPlainTextChunker chunker) : IFileTypeHandler
{
    public string Name => "PlainText";

    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt"
    };

    public IChunker Chunker { get; } = chunker;
}
