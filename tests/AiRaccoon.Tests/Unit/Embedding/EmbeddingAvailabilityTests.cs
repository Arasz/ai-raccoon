using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using EmbeddingAvailability = AiRaccoon.Setup.Models.EmbeddingAvailability;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>The startup bootstrap warns (never fails) when the bundled model is unavailable (see docs/work/features-native-memory/native-memory.feature).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingAvailabilityTests
{
    [Fact]
    public async Task EmbeddingAvailability_OnMissingAssets_LogsActionableWarning()
    {
        var logger = new FakeLogger<EmbeddingAvailability>();
        var availability = new EmbeddingAvailability(logger, new StubBundledModel(new BundledModelResult(false, ["e1"])));

        await availability.EnsureEmbeddingAvailabilityAsync(TestContext.Current.CancellationToken);

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Id.Id.ShouldBe(40);
        record.Message.ShouldContain("model embedding set local");
        record.Message.ShouldContain("e1");
    }

    [Fact]
    public async Task EmbeddingAvailability_OnAllPresent_LogsNothing()
    {
        var logger = new FakeLogger<EmbeddingAvailability>();
        var availability = new EmbeddingAvailability(logger, new StubBundledModel(new BundledModelResult(true, [])));

        await availability.EnsureEmbeddingAvailabilityAsync(TestContext.Current.CancellationToken);

        logger.Collector.Count.ShouldBe(0);
    }

    [Fact]
    public async Task EmbeddingAvailability_WhenEnsureThrows_LogsWarning()
    {
        // Pins the bare-catch path (docs/reviews/2026-08-05-integration-review-pr24-26.md): the boot
        // must continue with a warning, never a throw.
        var logger = new FakeLogger<EmbeddingAvailability>();
        var availability = new EmbeddingAvailability(logger, new ThrowingBundledModel());

        await availability.EnsureEmbeddingAvailabilityAsync(TestContext.Current.CancellationToken);

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Level.ShouldBe(LogLevel.Error);
        record.Id.Id.ShouldBe(41);
        record.Message.ShouldContain("model embedding set local");
    }

    private sealed class StubBundledModel(BundledModelResult result) : IBundledModel
    {
        public Task<BundledModelResult> EnsureAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);

        public Task<BundledModelResult> EnsureDownloadsAsync(string targetDirectory, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingBundledModel : IBundledModel
    {
        public Task<BundledModelResult> EnsureAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");

        public Task<BundledModelResult> EnsureDownloadsAsync(string targetDirectory, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }
}
