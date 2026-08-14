using System.Reflection;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     DI wiring for the self-learning noise substrate (ADR-0039): every piece is registered and
///     resolvable, and SqliteMemoryStore's constructor selection actually picks the 8-arg overload
///     (real INoiseShadowObserver) rather than silently falling back to the NoOp default that exists
///     only for TestData.CreateMemoryStore's 7-arg call.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoiseLearningRegistrationTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-di");

    public void Dispose() => Directory.Delete(_dataRoot, true);

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

        provider.GetRequiredService<INoiseClusterStore>().ShouldBeOfType<SqliteNoiseClusterStore>();
        provider.GetRequiredService<IContentEmbedder>().ShouldBeOfType<SqliteContentEmbedder>();
        provider.GetRequiredService<NoiseFeedbackCollector>().ShouldNotBeNull();
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
    public void RegisterCoreMemoryServices_IMemoryStore_UsesTheRealShadowObserver_NotTheNoOpDefault()
    {
        using var provider = BuildProvider();

        var store = provider.GetRequiredService<IMemoryStore>();
        store.ShouldBeOfType<SqliteMemoryStore>();

        var field = typeof(SqliteMemoryStore).GetField("_noiseShadowObserver", BindingFlags.NonPublic | BindingFlags.Instance);
        field.ShouldNotBeNull("SqliteMemoryStore must keep a _noiseShadowObserver field for this DI-wiring check to hold");
        var actualObserver = field!.GetValue(store);

        actualObserver.ShouldBeOfType<NoiseShadowObserver>(
            "production DI must select SqliteMemoryStore's 8-arg constructor, not silently fall back to NoOpNoiseShadowObserver");
    }
}
