using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.AI;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A scriptable <see cref="IEmbeddingService" /> for <c>CodeEmbedder</c> tests: deterministic
///     768-dim vectors (vec_code's fixed dimension) with no real model I/O, a settable
///     <see cref="TrimOverride" /> to exercise the query-trim wiring without a real manifest
///     tokenizer, and a settable <see cref="FailOnCreateGenerator" /> to simulate a
///     configured-but-unloadable engine (a broken manifest/model file) without seeding one on disk.
/// </summary>
public sealed class FakeCodeEmbeddingService : IEmbeddingService
{
    private readonly FakeGenerator _generator;

    public FakeCodeEmbeddingService() => _generator = new FakeGenerator(() => OnGenerateAsync, PoisonValues);

    public Func<Exception>? FailOnCreateGenerator { get; set; }

    public Func<string, string>? TrimOverride { get; set; }

    /// <summary>
    ///     S1 test seam: awaited inside the generator call, the exact window a real generation call
    ///     occupies between a drain's row SELECT and its per-row MarkCodeEmbedded UPDATE — lets a
    ///     test simulate a concurrent activation landing mid-batch without any production seam.
    /// </summary>
    public Func<Task>? OnGenerateAsync { get; set; }

    /// <summary>
    ///     S2 test seam: a call whose input values contain any of these strings throws — a batch
    ///     call fails whole (simulating one poison row breaking the whole generator call), and a
    ///     single-row retry of that same value fails too (simulating a row that can never embed).
    /// </summary>
    public HashSet<string> PoisonValues { get; } = new(StringComparer.Ordinal);

    /// <summary>Every generator call's input list, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Calls => _generator.Calls;

    public string EngineFingerprint(string provider, string? model, string? baseUrl) => $"test:{provider}:{model}";

    public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (FailOnCreateGenerator is { } fail)
        {
            throw fail();
        }

        return _generator;
    }

    public string TrimQueryToWindow(EmbeddingSettings settings, string query) => TrimOverride?.Invoke(query) ?? query;

    public int ResolveChunkBudgetFor(EmbeddingSettings settings) => 126;

    public int ResolveDimensions(EmbeddingSettings settings) => 768;

    public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) => null;

    private sealed class FakeGenerator(Func<Func<Task>?> onGenerateAsync, HashSet<string> poisonValues)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly List<IReadOnlyList<string>> _calls = [];

        public IReadOnlyList<IReadOnlyList<string>> Calls => _calls;

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(values);
            var items = values.ToList();
            _calls.Add(items);

            if (items.Any(poisonValues.Contains))
            {
                throw new InvalidOperationException("simulated poison row: this content can never embed");
            }

            if (onGenerateAsync() is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            var embeddings = new GeneratedEmbeddings<Embedding<float>>(items.Count);
            foreach (var text in items)
            {
                embeddings.Add(new Embedding<float>(DeterministicVector(text)));
            }

            return embeddings;
        }

        public void Dispose()
        {
        }

        object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) => null;

        private static float[] DeterministicVector(string text)
        {
            var vector = new float[768];
            var rnd = new Random(text.GetHashCode(StringComparison.Ordinal));
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)((rnd.NextDouble() * 2) - 1);
            }

            return vector;
        }
    }
}
