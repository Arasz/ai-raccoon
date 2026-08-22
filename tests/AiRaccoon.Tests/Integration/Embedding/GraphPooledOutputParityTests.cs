using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     When a model's ONNX graph publishes BOTH a token-level output and its own pooled output
///     (bge-m3: <c>token_embeddings</c> + <c>sentence_embedding</c>), the manifest picks one pooling
///     mode and the engine re-pools client-side. This gate pins the two against each other: the
///     engine's manifest-selected pooling must reproduce the graph's own pooled vector. A manifest
///     that names the wrong mode, or pooling that reads the wrong row, shows up here as a cosine
///     well below 1 — it is invisible to every shape or norm check, because a wrongly-pooled vector
///     is still the right length and still normalized.
///     <para>
///         Needs a real dual-output model on disk (bge-m3, ~2.27 GB): point
///         <see cref="ModelDirEnvVar" /> at a model directory carrying an
///         <see cref="EmbeddingManifest.FileName" />, or the test skips. Too large to ship with the
///         suite or run on every PR, so it runs only in <c>nightly.yml</c>'s <c>full-suite</c> job,
///         which downloads bge-m3 via <c>ai-raccoon model download BAAI/bge-m3</c> (the product's
///         own download path — <c>docs/how-to/configure-embedding-engines.md</c> Recipe 4), cached
///         across runs by <c>actions/cache</c>. Deliberately absent from <c>build.yml</c>'s PR-label
///         <c>build-nightly-gates</c> lane (see that job's own comment for why). To run locally:
///         <c>ai-raccoon model download BAAI/bge-m3 --dir &lt;dir&gt; --yes</c>, then
///         <c>AIRACCOON_POOLING_PARITY_MODEL_DIR=&lt;dir&gt; dotnet test --filter "FullyQualifiedName~GraphPooledOutputParityTests"</c>.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class GraphPooledOutputParityTests
{
    /// <summary>Absolute path to a local model directory whose graph publishes a pooled output.</summary>
    internal const string ModelDirEnvVar = "AIRACCOON_POOLING_PARITY_MODEL_DIR";

    /// <summary>
    ///     Mixed lengths and scripts in one batch: pooling reads a row indexed by the padded batch
    ///     length, so equal-length inputs would leave that arithmetic untested.
    /// </summary>
    private static readonly string[] MixedLengthBatch =
    [
        "vec0",
        "the drain reconciles vec0 to the engine's dimension before writing",
        "Wie heißt du? Ça va — naïve café, 東京 2026.",
        "sqlite hybrid search fuses the FTS leg and the vector leg with reciprocal rank fusion "
        + "before the relative score floor drops the tail of the candidate list"
    ];

    [Fact]
    public async Task TheEnginesPooledVector_ReproducesTheGraphsOwnPooledOutput()
    {
        var descriptor = LoadDescriptorOrSkip(out var modelDirectory);
        if (string.IsNullOrWhiteSpace(descriptor.EmbeddingOutput))
        {
            Assert.Skip($"'{descriptor.Model}' declares no onnx.embeddingOutput — nothing to compare pooling against");
        }

        var service = TestData.CreateEmbeddingService();
        var settings = new EmbeddingSettings("local", modelDirectory, null, null);
        var tokenizer = service.ResolveTokenizer(settings)!;
        using var generator = service.CreateGenerator(settings);

        var engine = await generator.GenerateAsync(MixedLengthBatch,
            cancellationToken: TestContext.Current.CancellationToken);

        using var session = new InferenceSession(Path.Combine(modelDirectory, descriptor.OnnxModelFile));
        for (var i = 0; i < MixedLengthBatch.Length; i++)
        {
            var graphPooled = RunGraphPooledOutput(session, descriptor, tokenizer, MixedLengthBatch[i]);
            var engineVector = engine[i].Vector.ToArray();

            engineVector.Length.ShouldBe(graphPooled.Length,
                $"input {i}: the engine and the graph must agree on the vector's length");
            Cosine(engineVector, Normalize(graphPooled)).ShouldBe(1.0, 1e-5,
                $"input {i}: pooling '{descriptor.Pooling}' over '{descriptor.TokenEmbeddingsOutput}' must reproduce "
                + $"the graph's own '{descriptor.EmbeddingOutput}' for model '{descriptor.Model}'");
        }
    }

    /// <summary>
    ///     The contract the engine owes every caller, checked against the real manifest rather than
    ///     a 384-dim stand-in: one vector per input, exactly <c>dimensions</c> long, finite, and on
    ///     the unit sphere when the manifest says <c>normalization=l2</c>.
    /// </summary>
    [Fact]
    public async Task TheEnginesOutput_IsOneManifestLengthNormalizedVectorPerInput()
    {
        var descriptor = LoadDescriptorOrSkip(out var modelDirectory);
        var service = TestData.CreateEmbeddingService();
        var settings = new EmbeddingSettings("local", modelDirectory, null, null);
        using var generator = service.CreateGenerator(settings);

        var result = await generator.GenerateAsync(MixedLengthBatch,
            cancellationToken: TestContext.Current.CancellationToken);

        service.ResolveDimensions(settings).ShouldBe(descriptor.Dimensions);
        result.Count.ShouldBe(MixedLengthBatch.Length, "one embedding per input, never per token");
        for (var i = 0; i < result.Count; i++)
        {
            var vector = result[i].Vector.ToArray();
            vector.Length.ShouldBe(descriptor.Dimensions,
                $"input {i}: manifest '{descriptor.Model}' declares {descriptor.Dimensions} dimensions");
            vector.ShouldAllBe(v => float.IsFinite(v), $"input {i}: every component must be finite");
            if (descriptor.Normalization == "l2")
            {
                Norm(vector).ShouldBe(1.0, 1e-5, $"input {i}: normalization=l2 must put the vector on the unit sphere");
            }
        }
    }

    private static EngineDescriptor LoadDescriptorOrSkip(out string modelDirectory)
    {
        var configured = Environment.GetEnvironmentVariable(ModelDirEnvVar);
        if (string.IsNullOrWhiteSpace(configured) || !Directory.Exists(configured))
        {
            Assert.Skip($"set {ModelDirEnvVar} to a local model directory containing {EmbeddingManifest.FileName} "
                        + "(a dual-output model such as bge-m3); the weights are too large to ship with the suite");
        }

        modelDirectory = Path.GetFullPath(configured);
        return new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(modelDirectory);
    }

    /// <summary>Runs the graph once and returns its own pooled output row for a single input.</summary>
    private static float[] RunGraphPooledOutput(
        InferenceSession session, EngineDescriptor descriptor, IEmbeddingTokenizer tokenizer, string text)
    {
        var ids = tokenizer.EncodeToIds(text, addSpecialTokens: true);
        var inputIds = new long[ids.Count];
        var mask = new long[ids.Count];
        for (var s = 0; s < ids.Count; s++)
        {
            inputIds[s] = ids[s];
            mask[s] = 1;
        }

        var feed = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, ids.Count])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, ids.Count]))
        };
        if (descriptor.InputNames.Contains("token_type_ids", StringComparer.Ordinal))
        {
            feed.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(new long[ids.Count], [1, ids.Count])));
        }

        using var results = session.Run(feed);
        var pooled = results.First(r => r.Name == descriptor.EmbeddingOutput).AsTensor<float>();
        pooled.Dimensions.Length.ShouldBe(2,
            $"'{descriptor.EmbeddingOutput}' is the graph's pooled output; it must be [batch, dimensions]");
        return pooled.ToArray();
    }

    private static double Norm(IReadOnlyList<float> vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        return Math.Sqrt(sum);
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = Norm(vector);
        return norm == 0 ? vector : [.. vector.Select(v => (float)(v / norm))];
    }

    private static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count)
        {
            return double.NaN;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
