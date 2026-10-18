using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     File type handler for Jupyter notebooks (.ipynb).
/// </summary>
public sealed class NotebookFileTypeHandler(INotebookChunker chunker) : IFileTypeHandler
{
    public string Name => "Notebook";

    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ipynb"
    };

    public IChunker Chunker { get; } = chunker;
}
