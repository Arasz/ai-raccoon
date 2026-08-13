using AiRaccoon.Core.Chunking;

namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Handler for a specific file format family and its associated chunker.
/// </summary>
public interface IFileTypeHandler
{
    string Name { get; }
    IReadOnlySet<string> Extensions { get; }
    IChunker Chunker { get; }
}
