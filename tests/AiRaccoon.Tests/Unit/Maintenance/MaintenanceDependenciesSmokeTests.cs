using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     DI smoke: the composition root hosts the bank-maintenance service exactly once on the
///     sole surviving host (the web host — stdio/plain hosts are deleted, so there is no
///     transport matrix anymore).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MaintenanceDependenciesSmokeTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void RegisterMemoryServices_HostsTheBankMaintenanceServiceOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        });

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().OfType<BankMaintenanceHostedService>().ShouldHaveSingleItem();
    }
}
