namespace AiRaccoon.Core.Embedding;

/// <summary>A model directory or declared model shape the caller supplied was checked and refused;
/// the message says why and how to fix it.</summary>
public sealed class EmbeddingModelRejectedException : InvalidOperationException
{
    public EmbeddingModelRejectedException(string message) : base(message)
    {
    }

    public EmbeddingModelRejectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
