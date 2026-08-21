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

    /// <summary>
    ///     The configured LOCAL engine's content-token chunk budget (D6/D9): 254 for bundled/legacy,
    ///     min(510, ctx − 2) for manifest models, 256 for non-local providers (unchanged).
    /// </summary>
    int ResolveChunkBudgetFor(EmbeddingSettings settings);

    /// <summary>
    ///     The tokenizer the configured LOCAL engine will embed with (D9): null for non-local
    ///     providers (they keep the chunker's default o200k proxy).
    /// </summary>
    IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings);
}
