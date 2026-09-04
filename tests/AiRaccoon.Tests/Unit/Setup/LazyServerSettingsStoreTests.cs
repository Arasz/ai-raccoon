using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using AiRaccoon.Settings;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP7: defers acquiring the settings backend until the first actual call, so a command that
///     never touches <see cref="ISettingsStore" /> — <c>serve</c> above all — never pays for probing
///     or auto-starting one, even though it is bound to this store like everything else outside the
///     write opt-out list (<see cref="CliWriteOptOuts" />).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LazyServerSettingsStoreTests
{
    [Fact]
    public async Task Construction_NeverInvokesAcquire()
    {
        var acquireCalls = 0;
        _ = new LazyServerSettingsStore(_ =>
        {
            acquireCalls++;
            return Task.FromResult<ISettingsStore>(new InMemorySettings());
        });

        acquireCalls.ShouldBe(0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetSettingAsync_TriggersAcquireExactlyOnce_AcrossMultipleCalls()
    {
        var acquireCalls = 0;
        var inner = new InMemorySettings();
        var store = new LazyServerSettingsStore(_ =>
        {
            acquireCalls++;
            return Task.FromResult<ISettingsStore>(inner);
        });

        await store.GetSettingAsync("a", TestContext.Current.CancellationToken);
        await store.GetSettingAsync("b", TestContext.Current.CancellationToken);
        await store.SetSettingAsync("c", "1", TestContext.Current.CancellationToken);

        acquireCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GetSettingAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings();
        inner.Values["known"] = "value";
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        (await store.GetSettingAsync("known", TestContext.Current.CancellationToken)).ShouldBe("value");
    }

    [Fact]
    public async Task SetSettingAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings();
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        await store.SetSettingAsync("k", "v", TestContext.Current.CancellationToken);

        inner.Values.ShouldContainKeyAndValue("k", "v");
    }

    [Fact]
    public async Task GetSettingsByPrefixAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings();
        inner.Values["p.a"] = "1";
        inner.Values["p.b"] = "2";
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        var rows = await store.GetSettingsByPrefixAsync("p.", TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DeleteSettingAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings();
        inner.Values["k"] = "v";
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        await store.DeleteSettingAsync("k", TestContext.Current.CancellationToken);

        inner.Values.ShouldNotContainKey("k");
    }

    /// <summary>A failed acquire must surface to the caller, never be swallowed or retried silently.</summary>
    [Fact]
    public async Task AFailingAcquire_PropagatesToTheCaller()
    {
        var store = new LazyServerSettingsStore(_ =>
            Task.FromException<ISettingsStore>(new SettingsServerUnavailableException("no server")));

        await Should.ThrowAsync<SettingsServerUnavailableException>(() => store.GetSettingAsync("k", TestContext.Current.CancellationToken));
    }

    /// <summary>ADR-0076: model set reaches the same acquired store as every other settings command.</summary>
    [Fact]
    public async Task StartModelMigrationAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings();
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        await store.StartModelMigrationAsync("local", "custom-path", null, TestContext.Current.CancellationToken);

        inner.LastModelMigrationRequest.ShouldBe(("local", "custom-path", (string?)null));
    }

    [Fact]
    public async Task StartModelMigrationAsync_TriggersTheSameAcquireAsASettingsCall()
    {
        var acquireCalls = 0;
        var inner = new InMemorySettings();
        var store = new LazyServerSettingsStore(_ =>
        {
            acquireCalls++;
            return Task.FromResult<ISettingsStore>(inner);
        });

        await store.StartModelMigrationAsync("local", null, null, TestContext.Current.CancellationToken);
        await store.GetSettingAsync("a", TestContext.Current.CancellationToken);

        acquireCalls.ShouldBe(1);
    }

    /// <summary>ADR-0075 amendment: repair reaches the same acquired store as every other control-plane call.</summary>
    [Fact]
    public async Task RequestRepairAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings();
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        await store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);

        inner.LastRepairRequest.ShouldBe(RepairKind.Reingest);
    }

    /// <summary>ADR-0099: the one-shot project-ids map forwards through the lazy store untouched.</summary>
    [Fact]
    public async Task RequestRepairAsync_ForwardsTheProjectIdsMapJson()
    {
        var inner = new InMemorySettings();
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));
        var mapJson = new AiRaccoon.Core.Projects.ProjectIdAliasMap(
            [new AiRaccoon.Core.Projects.ProjectIdAliasEntry("old-id", "new-id")], ["new-id"], []).ToJson();

        await store.RequestRepairAsync(RepairKind.ProjectIds, TestContext.Current.CancellationToken, mapJson);

        inner.LastRepairRequest.ShouldBe(RepairKind.ProjectIds);
        inner.LastRepairMapJson.ShouldBe(mapJson);
    }

    [Fact]
    public async Task ReportReingestAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings { ReingestReport = new ReingestRepairReport(3, 5, 7) };
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        var report = await store.ReportReingestAsync(TestContext.Current.CancellationToken);

        report.ShouldBe(new ReingestRepairReport(3, 5, 7));
    }

    [Fact]
    public async Task ReportChunkIndexAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings { ChunkIndexReport = new ChunkIndexRepairReport(2, 4, 6) };
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        var report = await store.ReportChunkIndexAsync(TestContext.Current.CancellationToken);

        report.ShouldBe(new ChunkIndexRepairReport(2, 4, 6));
    }

    // Ledger — project-ids-report-not-delegated : --filter ReportProjectIdsAsync_DelegatesToTheAcquiredStore : InMemorySettings report.
    [Fact]
    public async Task ReportProjectIdsAsync_DelegatesToTheAcquiredStore()
    {
        var expected = new ProjectIdCensusReport([], 1, 2, 3, 4, ["ingest.scope.global"]);
        var inner = new InMemorySettings { ProjectIdsReport = expected };
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        var report = await store.ReportProjectIdsAsync(TestContext.Current.CancellationToken);

        report.ShouldBe(expected);
    }

    [Fact]
    public async Task RequestRepairAsync_TriggersTheSameAcquireAsASettingsCall()
    {
        var acquireCalls = 0;
        var inner = new InMemorySettings();
        var store = new LazyServerSettingsStore(_ =>
        {
            acquireCalls++;
            return Task.FromResult<ISettingsStore>(inner);
        });

        await store.RequestRepairAsync(RepairKind.ChunkIndex, TestContext.Current.CancellationToken);
        await store.GetSettingAsync("a", TestContext.Current.CancellationToken);

        acquireCalls.ShouldBe(1);
    }

    /// <summary>ADR-0075 amendment: `noise entries` reaches the same acquired store as every other control-plane call.</summary>
    [Fact]
    public async Task SummarizeAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings
        {
            NoiseSummary = new NoiseEntrySummary(2, new Dictionary<string, int>(StringComparer.Ordinal) { ["p"] = 2 })
        };
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        var summary = await store.SummarizeAsync(TestContext.Current.CancellationToken);

        summary.ShouldBe(inner.NoiseSummary);
    }

    /// <summary>ADR-0075 amendment: `watch registered` reaches the same acquired store as every other control-plane call.</summary>
    [Fact]
    public async Task ListWatchesAsync_DelegatesToTheAcquiredStore()
    {
        var inner = new InMemorySettings { Watches = [new WatchRegistration("acme", "/a/b.md", 1, 2)] };
        var store = new LazyServerSettingsStore(_ => Task.FromResult<ISettingsStore>(inner));

        var watches = await store.ListWatchesAsync(TestContext.Current.CancellationToken);

        watches.ShouldBe(inner.Watches);
    }

    [Fact]
    public async Task SummarizeAsync_TriggersTheSameAcquireAsASettingsCall()
    {
        var acquireCalls = 0;
        var inner = new InMemorySettings();
        var store = new LazyServerSettingsStore(_ =>
        {
            acquireCalls++;
            return Task.FromResult<ISettingsStore>(inner);
        });

        await store.SummarizeAsync(TestContext.Current.CancellationToken);
        await store.GetSettingAsync("a", TestContext.Current.CancellationToken);

        acquireCalls.ShouldBe(1);
    }
}
