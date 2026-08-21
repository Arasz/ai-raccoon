using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.AI;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     An <see cref="IEmbeddingService" /> that records every generator call it receives instead of
///     talking to a model or a network endpoint. Driven through a store built by
///     <see cref="TestData.CreateMemoryStore" /> with no hosted service in play, it lets a test prove
///     "one write embeds its content exactly once" deterministically — no background sweep can ever
///     run alongside it.
/// </summary>
public sealed class CountingEmbeddingService : IEmbeddingService
{
    public string EngineFingerprint(string provider, string? model, string? baseUrl) =>
        $"test:{provider}:{model}@{baseUrl}";
    private readonly CountingGenerator _generator = new();

    /// <summary>Every generator call's input list, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Calls => _generator.Calls;

    public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return _generator;
    }

    public string TrimQueryToWindow(EmbeddingSettings settings, string query) => query;

    public int ResolveChunkBudgetFor(EmbeddingSettings settings) => OnnxEmbeddingGenerator.MaxContentTokens;

    /// <summary>The real bundled tokenizer — the resolver contract says local ⇒ a tokenizer exists (D9).</summary>
    public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) =>
        string.Equals(settings.Provider, "local", StringComparison.OrdinalIgnoreCase)
            ? WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath())
            : null;

    /// <summary>How many calls embedded exactly <paramref name="value" /> alone (no heading path riding along).</summary>
    public int CallCountFor(string value) => Calls.Count(inputs => inputs.Count == 1 && inputs[0] == value);

    private sealed class CountingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly List<IReadOnlyList<string>> _calls = [];

        public IReadOnlyList<IReadOnlyList<string>> Calls => _calls;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(values);
            var items = values.ToList();
            _calls.Add(items);

            var embeddings = new GeneratedEmbeddings<Embedding<float>>(items.Count);
            foreach (var _ in items)
            {
                embeddings.Add(new Embedding<float>(new float[384]));
            }

            return Task.FromResult(embeddings);
        }

        public void Dispose()
        {
        }

        object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) => null;
    }
}
