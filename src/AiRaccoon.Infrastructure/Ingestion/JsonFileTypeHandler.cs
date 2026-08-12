using AiRaccoon.Core.Chunking;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     File type handler for JSON files (.json).
/// </summary>
public sealed class JsonFileTypeHandler(IChunker chunker) : IFileTypeHandler
{
    public string Name => "Json";

    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".json"
    };

    public IChunker Chunker { get; } = chunker;
}
