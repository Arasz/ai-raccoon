using AiRaccoon.Core.Memory;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FakeMemoryStoreTests
{
    [Fact]
    public async Task UnOverriddenMemberThrowsNamingItself()
    {
        IMemoryStore store = new FakeMemoryStore();

        var thrown = await Should.ThrowAsync<NotSupportedException>(
            () => store.GetStatsAsync("acme"));

        thrown.Message.ShouldContain(nameof(IMemoryStore.GetStatsAsync));
    }

    [Fact]
    public async Task OverriddenMemberIsUsedInsteadOfThrowing()
    {
        IMemoryStore store = new StatsStore();

        var stats = await store.GetStatsAsync("acme", TestContext.Current.CancellationToken);

        stats.EntryCount.ShouldBe(7);
    }

    /// <summary>DeleteInScopeAsync has no base override, so the interface default must still reach DeleteAsync.</summary>
    [Fact]
    public async Task DeleteInScopeRoutesToDeleteOverride()
    {
        IMemoryStore store = new DeletingStore();

        var deleted = await store.DeleteInScopeAsync("acme", "hash", "project",
            TestContext.Current.CancellationToken);

        deleted.ShouldBeTrue();
    }

    /// <summary>The one member that does not throw by default (WP3): a subject batching settings reads must not force every fake to override it.</summary>
    [Fact]
    public async Task GetSettingsByPrefixAsync_WithNoOverride_ReturnsEmptyRatherThanThrowing()
    {
        IMemoryStore store = new FakeMemoryStore();

        var settings = await store.GetSettingsByPrefixAsync("access.mode.", TestContext.Current.CancellationToken);

        settings.ShouldBeEmpty();
    }

    private sealed class StatsStore : FakeMemoryStore
    {
        public override Task<MemoryStats> GetStatsAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryStats(7, 0, []));
    }

    private sealed class DeletingStore : FakeMemoryStore
    {
        public override Task<bool> DeleteAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
