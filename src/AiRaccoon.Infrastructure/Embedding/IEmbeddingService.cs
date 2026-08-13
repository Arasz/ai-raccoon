using Microsoft.Extensions.AI;

namespace AiRaccoon.Infrastructure.Embedding;

public interface IEmbeddingService
{
    IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings);
}
