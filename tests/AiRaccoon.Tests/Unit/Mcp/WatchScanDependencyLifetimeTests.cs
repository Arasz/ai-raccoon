using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     R8 (docs/plans/2026-08-07-watch-scan-runaway-fix.md): WatchScanGuard and the scan lease
///     must resolve as the same instance on every ask. Dependencies.cs:137-141 documents why —
///     a transient guard silently defeats single-flight, and a transient lease hands every call a
///     new owner so renew/release could never match — but a comment enforces nothing by itself.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchScanDependencyLifetimeTests : IDisposable
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
    public void RegisterMemoryServices_ResolvesTheScanGuardAsASingleton()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<WatchScanGuard>();
        var second = provider.GetRequiredService<WatchScanGuard>();

        second.ShouldBeSameAs(first);
    }

    [Fact]
    public void RegisterMemoryServices_ResolvesTheScanLeaseAsASingleton()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IWatchScanLease>();
        var second = provider.GetRequiredService<IWatchScanLease>();

        second.ShouldBeSameAs(first);
    }
}
