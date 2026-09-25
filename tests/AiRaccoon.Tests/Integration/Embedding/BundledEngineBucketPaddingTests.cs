using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0114: an MLX-backed generator pads each row to a 64-token bucket with a zero attention
///     mask, and the bundled engine's vector does not change. The MLX branch is reached through the
///     generator's executor seam over a CPU session, so this runs without the MLX plugin.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BundledEngineBucketPaddingTests
{
    public static TheoryData<int> Words => [3, 40, 150];

    [RetryTheory]
    [MemberData(nameof(Words))]
    public async Task BucketedRow_GivesTheUnpaddedVector(int words)
    {
        var text = string.Join(' ', Enumerable.Range(0, words).Select(i => $"token{i % 17} drains pending rows"));
        using var plain = Generator();
        using var bucketed = Generator();
        bucketed.AttachMlxExecutorForTesting(new SingleThreadExecutor("bucket-padding-test"));

        var expected = await plain.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
        var actual = await bucketed.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);

        (bucketed.LastSequenceLength % 64).ShouldBe(0);
        bucketed.LastSequenceLength.ShouldBeGreaterThan(plain.LastSequenceLength);
        TestData.Cosine(actual[0].Vector, expected[0].Vector).ShouldBeGreaterThanOrEqualTo(0.99999);
    }

    [RetryFact]
    public async Task CpuSession_RunsTheRowAtItsOwnLength()
    {
        using var plain = Generator();

        await plain.GenerateAsync(["one short row"], cancellationToken: TestContext.Current.CancellationToken);

        (plain.LastSequenceLength % 64).ShouldNotBe(0);
    }

    private static OnnxEmbeddingGenerator Generator()
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(directory);
        return new OnnxEmbeddingGenerator(Path.Combine(directory, descriptor.OnnxModelFile),
            new EmbeddingTokenizerFactory().Create(descriptor, directory), descriptor, NullLogger.Instance);
    }
}
