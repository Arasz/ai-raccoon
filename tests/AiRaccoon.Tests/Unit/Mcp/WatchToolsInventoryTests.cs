using System.Reflection;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     WatchTools tool inventory: exactly the three spec tools.
///     The existing ToolInventoryTests (18 MemoryTools tools) stays untouched — WatchTools
///     is a separate class with its own inventory test (docs/plans/file-watcher-implementation.md §6).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchToolsInventoryTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void WatchTools_ExposesAll3SpecTools()
    {
        var tools = typeof(WatchTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .ToList();

        tools.Count.ShouldBe(3);
        tools.ShouldContain("add_watch");
        tools.ShouldContain("get_watch_status");
        tools.ShouldContain("remove_watch");
    }

    /// <summary>DI smoke (docs/plans/file-watcher-implementation.md §S6): the composition root resolves IWatchService.</summary>
    [Fact]
    public void RegisterMemoryServices_ResolvesIWatchService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWatchService>().ShouldNotBeNull();
    }
}
