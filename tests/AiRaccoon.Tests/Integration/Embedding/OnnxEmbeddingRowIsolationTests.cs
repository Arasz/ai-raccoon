using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Each row runs through the ONNX session on its own: a text's vector is bit-identical whether it
///     is embedded alone or alongside longer texts, because it is never padded to a batch-mate's length.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class OnnxEmbeddingRowIsolationTests : IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [RetryFact]
    public async Task ShortText_BatchedWithLongerText_EmbedsExactlyAsWhenAlone()
    {
        using var generator = TestData.CreateEmbeddingService().CreateGenerator(
            new EmbeddingSettings("local", null, null, null));
        const string shortText = "short zebra note";
        var longText = string.Join(' ', Enumerable.Repeat("memory retrieval ranking evidence", 40));

        var alone = await generator.GenerateAsync([shortText],
            cancellationToken: TestContext.Current.CancellationToken);
        var batched = await generator.GenerateAsync([shortText, longText],
            cancellationToken: TestContext.Current.CancellationToken);

        batched.Count.ShouldBe(2);
        batched[0].Vector.ToArray().ShouldBe(alone[0].Vector.ToArray(),
            "a row must never be padded to a batch-mate's length and run in a shared session call");
    }
}
