using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     DI smoke: the composition root hosts the bank-maintenance service exactly once
///     in EVERY transport shape — a stdio process is session-bound, so its boundary
///     checkpoints (startup + shutdown) are the only maintenance it gets.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MaintenanceDependenciesSmokeTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Theory]
    [InlineData(McpTransport.Stdio)]
    [InlineData(McpTransport.Http)]
    public void RegisterMemoryServices_HostsTheBankMaintenanceServiceOnce(McpTransport transport)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(transport));

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().OfType<BankMaintenanceHostedService>().ShouldHaveSingleItem();
    }
}
