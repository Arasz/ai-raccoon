using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     S5 DI smoke (the S6-forwarded half): the composition root resolves the catch-up and the
///     event source, and hosts the watcher re-watch service.
/// </summary>
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
}
