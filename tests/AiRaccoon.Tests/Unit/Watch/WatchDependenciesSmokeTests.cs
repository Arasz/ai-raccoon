using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>The composition root resolves the catch-up and the event source, and hosts the watcher re-watch service.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchDependenciesSmokeTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public void RegisterMemoryServices_ResolvesCatchUpEventSource_AndHostsTheWatcherService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Http));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<WatchCatchUp>().ShouldNotBeNull();
        provider.GetRequiredService<WatchEventSource>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().OfType<WatchHostedService>().ShouldHaveSingleItem();
    }

    /// <summary>A transient registration would give each consumer its own scan guard and lease owner, defeating the per-(project, path) single-flight guarantee.</summary>
    [Fact]
    public void RegisterMemoryServices_SharesOneScanGuard_AndOneScanLease()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Http));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<WatchScanGuard>()
            .ShouldBeSameAs(provider.GetRequiredService<WatchScanGuard>());
        provider.GetRequiredService<IWatchScanLease>()
            .ShouldBeSameAs(provider.GetRequiredService<IWatchScanLease>());
    }
}
