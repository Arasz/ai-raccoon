using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Promotion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

/// <summary>
///     The queue validates its own arguments: the MCP tool is one caller among several
///     (the background loop is another), so the rules cannot live only in the transport layer.
///     Every fake here throws, which proves the guard fires before any dependency is touched.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionQueueServiceGuardTests
{
    private static PromotionQueueService NewService() =>
        new(new UnreachableQueueStore(), new FakeExtractionStore(), new UniformCountEvictionPolicy(),
            new UnreachableMetrics(), NullLogger<PromotionQueueService>.Instance,
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    private static readonly IReadOnlyList<QueueCandidate> OneCandidate =
        [new("h1", "h1.md", "a fact", null, 3.0, ["organic-write"])];

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProposeAsync_RejectsABlankProjectId(string projectId)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            NewService().ProposeAsync(projectId, OneCandidate, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProposeAsync_RejectsNullCandidates()
    {
        await Should.ThrowAsync<ArgumentNullException>(() =>
            NewService().ProposeAsync("acme", null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PromoteAsync_RejectsAnEmptyProjectList()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            NewService().PromoteAsync([], 20, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PromoteAsync_RejectsANonPositiveLimit(int limit)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            NewService().PromoteAsync(["acme"], limit, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListAsync_RejectsANonPositiveLimit(int limit)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            NewService().ListAsync("acme", limit, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DiscardAsync_RejectsABlankProjectId(string projectId)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            NewService().DiscardAsync(projectId, "h1", TestContext.Current.CancellationToken));
    }

    private sealed class UnreachableQueueStore : IPromotionQueueStore
    {
        public Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PromotionQueueRow?> EvictVictimAsync(string projectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnreachableMetrics : IPromotionQueueMetrics
    {
        public void RecordEviction(string projectId, double victimScore, string reason) =>
            throw new NotSupportedException();

        public void RecordPromoted(string projectId, double waitSeconds) => throw new NotSupportedException();

        public void RecordDiscarded(string projectId, double waitSeconds) => throw new NotSupportedException();

        public void RecordSnapshot(PromotionQueueStats stats, int capacity) => throw new NotSupportedException();
    }
}
