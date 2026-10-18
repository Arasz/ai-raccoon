namespace AiRaccoon.Core.Chunking;

/// <summary>Chunker for Jupyter notebooks; indexes cell sources only, never outputs.</summary>
public interface INotebookChunker : IChunker
{
}
