using Microsoft.Extensions.AI;

namespace AiRaccoon.Infrastructure.Embedding;

public interface IEmbeddingService
{
    IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings);

    /// <summary>
    ///     Trims a search query to the model's content window, reporting it. Deliberate here rather
    ///     than silent inside the generator: the generator cannot tell a query from stored content,
    ///     so it reported both through one event and called both a "chunk" (ADR-0071).
    /// </summary>
    string TrimQueryToWindow(EmbeddingSettings settings, string query);
}
