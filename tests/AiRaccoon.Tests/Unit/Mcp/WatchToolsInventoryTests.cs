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
///     WatchTools tool inventory: exactly the three spec tools, each named by a TN_* const.
///     The existing ToolInventoryTests (18 MemoryTools tools) stays untouched — WatchTools
///     is a separate class with its own inventory test (see docs/plans/file-watcher-implementation.md §6, R8).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchToolsInventoryTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

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
        tools.ShouldContain("memory_watch_add");
        tools.ShouldContain("memory_watch_status");
        tools.ShouldContain("memory_watch_remove");
    }

    [Fact]
    public void WatchToolNames_MatchConstStrings()
    {
        var constFields = typeof(WatchTools)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.StartsWith("Tn"))
            .ToList();

        var constValues = new Dictionary<string, string>();
        foreach (var f in constFields)
        {
            var val = f.GetRawConstantValue() as string;
            if (val is not null)
            {
                constValues[val] = f.Name;
            }
        }

        var toolMethods = typeof(WatchTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => new
            {
                Method = m,
                Attr = m.GetCustomAttribute<McpServerToolAttribute>()
            })
            .Where(x => x.Attr is not null)
            .ToList();

        toolMethods.Count.ShouldBe(3);

        foreach (var tm in toolMethods)
        {
            var toolName = tm.Attr!.Name!;
            constValues.ShouldContainKey(toolName,
                $"Missing const for tool '{toolName}' (method: {tm.Method.Name})");
            constValues[toolName].ShouldStartWith("Tn");
        }
    }

    /// <summary>DI smoke (see docs/plans/file-watcher-implementation.md §S6, TDD order 4): the composition root resolves IWatchService.</summary>
    [Fact]
    public void RegisterMemoryServices_ResolvesIWatchService()
    {
        var services = new ServiceCollection();
        // Mirror the host: WebApplication.CreateBuilder registers logging before
        // RegisterMemoryServices runs (WatchPipeline takes ILogger<WatchPipeline>).
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
