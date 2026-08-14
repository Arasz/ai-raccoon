using System.Buffers;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Padding determinism: a short text's embedding must not depend on ArrayPool reuse or on
///     the longer texts it is batched with — the padding region must read as zeros.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class OnnxEmbeddingPaddingTests : IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task SameBatchTwice_ProducesIdenticalEmbeddings_EvenAfterPoolReuse()
    {
        using var generator = TestData.CreateEmbeddingService().CreateGenerator(
            new EmbeddingSettings("local", null, null, null));

        var longText = string.Join(' ', Enumerable.Repeat("memory retrieval ranking evidence", 40));
        string[] batch = ["short zebra note", longText];

        var first = await generator.GenerateAsync(batch,
            cancellationToken: TestContext.Current.CancellationToken);

        DirtyTheSharedPool();

        var second = await generator.GenerateAsync(batch,
            cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < batch.Length; i++)
        {
            var drift = 1.0 - TestData.Cosine(first[i].Vector, second[i].Vector);
            drift.ShouldBeLessThan(1e-6,
                $"embedding {i} must not depend on what a pooled buffer previously held");
        }
    }

    /// <summary>Fills and returns pool buffers in the size buckets RunBatch rents, so a dirty rental is deterministic.</summary>
    private static void DirtyTheSharedPool()
    {
        var rented = new List<long[]>();
        for (var i = 0; i < 16; i++)
        {
            foreach (var size in new[] { 128, 256, 512, 1024, 2048, 4096 })
            {
                var buffer = ArrayPool<long>.Shared.Rent(size);
                Array.Fill(buffer, long.MaxValue);
                rented.Add(buffer);
            }
        }

        foreach (var buffer in rented)
        {
            ArrayPool<long>.Shared.Return(buffer);
        }
    }
}
