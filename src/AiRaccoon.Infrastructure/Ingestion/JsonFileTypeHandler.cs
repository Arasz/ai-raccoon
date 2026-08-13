using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     File type handler for JSON files (.json).
/// </summary>
public sealed class JsonFileTypeHandler(IJsonChunker chunker) : IFileTypeHandler
{
    public string Name => "Json";

    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".json"
    };

    public IChunker Chunker { get; } = chunker;
}
