using System.Reflection;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     DI wiring for the noise substrate (ADR-0029/ADR-0039, amended): the training-data store, the
///     detector seam, and shadow-mode plumbing are all registered and resolvable, and
///     SqliteMemoryStore's constructor selection actually picks the 9-arg overload (real
///     INoiseShadowObserver + INoiseEntryStore) rather than silently falling back to the NoOp
///     defaults that exist only for TestData.CreateMemoryStore's 7-arg call.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoiseLearningRegistrationTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-di");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(_dataRoot));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RegisterCoreMemoryServices_ResolvesTheNoiseSubstrate()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<INoiseEntryStore>().ShouldBeOfType<SqliteNoiseEntryStore>();
        provider.GetRequiredService<INoiseShadowObserver>().ShouldBeOfType<NoiseShadowObserver>();
    }

    [Fact]
    public void RegisterCoreMemoryServices_NoDetectorIsRegisteredExceptTheNoOp()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<INoiseDetector>().ShouldBeOfType<NoOpNoiseDetector>(
            "ADR-0039: no scoring model ships yet — the seam must resolve to the no-op until a detector is validated");
    }

    [Fact]
    public void RegisterCoreMemoryServices_IMemoryStore_UsesTheRealObservers_NotTheNoOpDefaults()
    {
        using var provider = BuildProvider();

        var store = provider.GetRequiredService<IMemoryStore>();
        store.ShouldBeOfType<SqliteMemoryStore>();

        var shadowField = typeof(SqliteMemoryStore).GetField("_noiseShadowObserver", BindingFlags.NonPublic | BindingFlags.Instance);
        shadowField.ShouldNotBeNull("SqliteMemoryStore must keep a _noiseShadowObserver field for this DI-wiring check to hold");
        shadowField!.GetValue(store).ShouldBeOfType<NoiseShadowObserver>(
            "production DI must select SqliteMemoryStore's 9-arg constructor, not silently fall back to NoOpNoiseShadowObserver");

        var entryStoreField = typeof(SqliteMemoryStore).GetField("_noiseEntryStore", BindingFlags.NonPublic | BindingFlags.Instance);
        entryStoreField.ShouldNotBeNull("SqliteMemoryStore must keep a _noiseEntryStore field for this DI-wiring check to hold");
        entryStoreField!.GetValue(store).ShouldBeOfType<SqliteNoiseEntryStore>(
            "production DI must select SqliteMemoryStore's 9-arg constructor, not silently fall back to NoOpNoiseEntryStore");
    }
}
