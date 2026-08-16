using AiRaccoon.Core.Memory;
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
            Task.FromException<ISettingsStore>(new SettingsServerUnavailableException("no server", null)));

        await Should.ThrowAsync<SettingsServerUnavailableException>(
            () => store.GetSettingAsync("k", TestContext.Current.CancellationToken));
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
}
