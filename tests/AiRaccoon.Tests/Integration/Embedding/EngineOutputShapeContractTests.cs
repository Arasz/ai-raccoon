using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The engine's OUTPUT CONTRACT, independent of which model or pooling mode is configured:
///     one vector per input, exactly <c>dimensions</c> long, finite, and L2-normalized when
///     <c>normalization=l2</c>. The pooling mode chooses which numbers come out; it may never
///     change how many, and the bundled 384-dim model is a sufficient fixture for that — no large
///     model needed. This is the check that a generator returning an unpooled
///     <c>[sequence × dimensions]</c> row (a 2048-long "vector" for a 2-token input) or raw
///     unnormalized hidden state (norms in the 1e9 range) trips, whatever produced it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EngineOutputShapeContractTests : IAsyncLifetime
{
    /// <summary>
    ///     Deliberately different token lengths: a batch pads to the longest sequence and the
    ///     pooled-row arithmetic is indexed by that padded length, so a single-item or
    ///     equal-length batch would leave the length term untested.
    /// </summary>
    private static readonly string[] MixedLengthBatch =
    [
        "vec0",
        "the drain reconciles vec0 to the engine's dimension",
        "sqlite hybrid search fuses the FTS leg and the vector leg with reciprocal rank fusion "
        + "before the relative score floor drops the tail of the candidate list"
    ];

    public async ValueTask InitializeAsync() =>
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static OnnxEmbeddingGenerator Build(string pooling, string normalization) => new(
        BundledModel.ResolveModelPath(),
        WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()),
        EmbeddingService.BundledDescriptor with { Pooling = pooling, Normalization = normalization },
        NullLogger<OnnxEmbeddingGenerator>.Instance);

    [Theory]
    [InlineData("mean")]
    [InlineData("cls")]
    public async Task EveryPoolingMode_ReturnsOneVectorPerInput_OfTheDeclaredLength(string pooling)
    {
        var declared = EmbeddingService.BundledDescriptor.Dimensions;
        using var generator = Build(pooling, "l2");

        var result = await generator.GenerateAsync(MixedLengthBatch,
            cancellationToken: TestContext.Current.CancellationToken);

        generator.Dimension.ShouldBe(declared, "the session's reported dimension must match the descriptor's");
        result.Count.ShouldBe(MixedLengthBatch.Length, "one embedding per input, never per token");
        for (var i = 0; i < result.Count; i++)
        {
            result[i].Vector.Length.ShouldBe(declared,
                $"input {i} ({CountTokens(MixedLengthBatch[i])} tokens) pooled to {result[i].Vector.Length} values "
                + $"under '{pooling}'; a pooled vector is 'dimensions' long regardless of sequence length");
        }
    }

    [Theory]
    [InlineData("mean")]
    [InlineData("cls")]
    public async Task L2Normalization_LeavesEveryVectorOnTheUnitSphere_AndFinite(string pooling)
    {
        using var generator = Build(pooling, "l2");

        var result = await generator.GenerateAsync(MixedLengthBatch,
            cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < result.Count; i++)
        {
            var vector = result[i].Vector.ToArray();
            vector.ShouldAllBe(v => float.IsFinite(v), $"input {i}: every component must be finite under '{pooling}'");
            Norm(vector).ShouldBe(1.0, 1e-5,
                $"input {i}: normalization=l2 must put the vector on the unit sphere under '{pooling}'");
        }
    }

    /// <summary>
    ///     The length contract is a property of pooling, not of normalization — turning
    ///     normalization off must not change how many numbers come back, only their scale.
    /// </summary>
    [Theory]
    [InlineData("mean")]
    [InlineData("cls")]
    public async Task NormalizationNone_KeepsTheLengthContract_AndOnlyChangesScale(string pooling)
    {
        var declared = EmbeddingService.BundledDescriptor.Dimensions;
        using var normalized = Build(pooling, "l2");
        using var raw = Build(pooling, "none");

        var normalizedResult = await normalized.GenerateAsync(MixedLengthBatch,
            cancellationToken: TestContext.Current.CancellationToken);
        var rawResult = await raw.GenerateAsync(MixedLengthBatch,
            cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < MixedLengthBatch.Length; i++)
        {
            rawResult[i].Vector.Length.ShouldBe(declared, $"input {i}: normalization must not change the length");
            // Secondary observable: same direction, different scale — the unnormalized vector is
            // the same pooled row, so normalizing it must reproduce the normalized engine's output.
            Cosine(Normalize(rawResult[i].Vector.ToArray()), normalizedResult[i].Vector.ToArray())
                .ShouldBe(1.0, 1e-5, $"input {i}: normalization must not rotate the vector under '{pooling}'");
        }
    }

    /// <summary>
    ///     Length and norm alone are satisfied by a constant: distinct inputs must also produce
    ///     distinct, non-degenerate vectors, or the contract passes over an engine that returns the
    ///     same buffer (or a zero vector) for everything.
    /// </summary>
    [Theory]
    [InlineData("mean")]
    [InlineData("cls")]
    public async Task DistinctInputsInOneBatch_ProduceDistinctNonZeroVectors(string pooling)
    {
        using var generator = Build(pooling, "l2");

        var result = await generator.GenerateAsync(MixedLengthBatch,
            cancellationToken: TestContext.Current.CancellationToken);

        var vectors = Enumerable.Range(0, result.Count).Select(i => result[i].Vector.ToArray()).ToList();
        foreach (var vector in vectors)
        {
            vector.ShouldContain(v => v != 0f, "a pooled vector of an in-vocabulary sentence is never all zeros");
        }

        for (var i = 0; i < vectors.Count; i++)
        {
            for (var j = i + 1; j < vectors.Count; j++)
            {
                Cosine(vectors[i], vectors[j]).ShouldBeLessThan(0.999,
                    $"inputs {i} and {j} are different sentences but pooled to the same direction under '{pooling}' "
                    + "— the batch rows are being read from one slice");
            }
        }
    }

    private static int CountTokens(string text) =>
        WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()).EncodeToIds(text, addSpecialTokens: true).Count;

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
