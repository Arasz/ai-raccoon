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

    /// <summary>
    ///     Stable engine identity recorded in settings; a change re-embeds the bank (D7: local
    ///     manifest directories hash the manifest's content including per-file sha256s). Instance
    ///     member — the fingerprint touches the filesystem, so it lives on the injected service,
    ///     not in a static class.
    /// </summary>
    string EngineFingerprint(string provider, string? model, string? baseUrl);

    /// <summary>
    ///     The dimension the configured engine embeds at, so the drain can reconcile vec0 before it
    ///     writes (D3): the manifest's value for a local model directory, 384 for bundled/legacy, and
    ///     the declared `embedding.dimensions` for a remote provider.
    /// </summary>
    int ResolveDimensions(EmbeddingSettings settings);
}
