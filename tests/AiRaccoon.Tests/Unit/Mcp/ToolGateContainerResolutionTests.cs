using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tools;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     DI smoke for #111: ToolGate and every tool class that depends on it must be constructible
///     from the real composition root, not just from a hand-built `new ToolGate(guard, queue)` in
///     each tool's own unit tests. Removing ToolGate's registration must fail here, not at the
///     first production MCP call.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolGateContainerResolutionTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Http));

        return services.BuildServiceProvider();
    }

    [Fact]
    public void RegisterMemoryServices_ResolvesToolGate()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<ToolGate>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData(typeof(MemoryTools))]
    [InlineData(typeof(PromotionTools))]
    [InlineData(typeof(ShareTools))]
    [InlineData(typeof(SweepTools))]
    [InlineData(typeof(SyncTools))]
    [InlineData(typeof(WatchTools))]
    [InlineData(typeof(WorkspaceTools))]
    public void RegisterMemoryServices_ConstructsEveryToolClass(Type toolType)
    {
        using var provider = BuildProvider();

        ActivatorUtilities.CreateInstance(provider, toolType).ShouldNotBeNull();
    }
}
