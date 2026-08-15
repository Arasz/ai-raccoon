using AiRaccoon.Core.Memory;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP9: SqliteMemoryStore must take the DI-registered ISettingsStore rather than hand-building
///     its own SqliteSettingsStore. A recording decorator registered after RegisterCoreMemoryServices
///     overrides the production ISettingsStore registration; if SqliteMemoryStore still `new`'d its
///     own instance internally, the decorator would never observe a call made through IMemoryStore's
///     settings members. Writing then reading a setting is not a discriminating test here, because
///     both a hand-built and an injected SqliteSettingsStore hit the same on-disk bank.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SettingsStoreInjectionTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-settings-di");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task RegisterCoreMemoryServices_IMemoryStore_UsesTheRegisteredSettingsStore()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(_dataRoot));

        var recorder = new RecordingSettingsStore();
        services.AddSingleton<ISettingsStore>(recorder);

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IMemoryStore>();

        await store.GetSettingAsync("probe-key", TestContext.Current.CancellationToken);

        recorder.GetSettingKeys.ShouldContain("probe-key",
            "SqliteMemoryStore must resolve ISettingsStore from the container, not construct its own");
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public List<string> GetSettingKeys { get; } = [];

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            GetSettingKeys.Add(key);
            return Task.FromResult<string?>(null);
        }

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
