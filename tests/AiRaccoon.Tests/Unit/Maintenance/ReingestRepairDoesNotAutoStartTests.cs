using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     Repair reingest's own explicit-only constraint (same as ChunkIndexRepair, GH #371): it deletes
///     and re-inserts a project's chunks, so <see cref="MaintenanceJobRunner" />/
///     <see cref="BankMaintenanceHostedService" /> — which can start jobs within seconds of the first
///     bank open — must never be able to reach it. Two independent proofs: it cannot structurally BE
///     one of those jobs, and the real DI-composed job list does not contain one.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReingestRepairDoesNotAutoStartTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void ReingestRepair_DoesNotImplementIMaintenanceJob() =>
        typeof(ReingestRepair).GetInterfaces().ShouldNotContain(typeof(IMaintenanceJob),
            "a type wired into the auto-run job list must be an IMaintenanceJob — this one structurally cannot be added to it by accident");

    [Fact]
    public void RegisterMemoryServices_TheBankOpenJobList_DoesNotIncludeTheReingestRepair()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Stdio));

        using var provider = services.BuildServiceProvider();

        var jobs = provider.GetRequiredService<IReadOnlyList<IMaintenanceJob>>();
        jobs.ShouldNotBeEmpty();
        jobs.ShouldAllBe(job => !job.Name.Contains("reingest", StringComparison.OrdinalIgnoreCase)
                                 && !job.DisplayName.Contains("reingest", StringComparison.OrdinalIgnoreCase));
    }
}
