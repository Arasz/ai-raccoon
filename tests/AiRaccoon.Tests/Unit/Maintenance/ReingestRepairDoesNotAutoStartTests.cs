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
///     Repair reingest's own explicit-only constraint (same as ChunkIndexRepair, GH #371): a repair
///     must never run because a clock fired, only because a human explicitly asked. That guard is NOT
///     "never appear on the maintenance job list" — ADR-0075's amendment puts
///     <see cref="ReingestRepairJob" /> on that list, on-demand-only, mirroring
///     <see cref="ModelMigrationJob" /> (ADR-0076): its <see cref="IMaintenanceJob.Interval" /> is
///     null, so <see cref="MaintenanceJobRunner" />/<see cref="BankMaintenanceHostedService" /> —
///     which can start jobs within seconds of the first bank open — can only ever run it when
///     <see cref="IMaintenanceJob.HasWorkAsync" /> finds a repair_requests row `repair reingest
///     --apply` committed through the server (<see cref="ReingestRepairJobTests" /> covers that
///     gating directly). Two proofs here: <see cref="ReingestRepair" /> itself structurally cannot BE
///     one of those jobs, and the real DI-composed job list's entry for it is on-demand-only.
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
            "a type wired into the auto-run job list must be an IMaintenanceJob — this one structurally cannot be added to it by accident; ReingestRepairJob wraps it instead");

    [Fact]
    public void RegisterMemoryServices_TheBankOpenJobList_HasNoClockIntervalForReingestRepair()
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
        var reingestJobs = jobs.Where(job => job.Name.Contains("reingest", StringComparison.OrdinalIgnoreCase)).ToList();
        reingestJobs.ShouldHaveSingleItem("exactly one on-demand relay for the CLI-requested reingest repair, mirroring ModelMigrationJob");
        reingestJobs.Single().Interval.ShouldBeNull(
            "never clock-scheduled — HasWorkAsync (gated on a repair_requests row) is the only due-ness signal");
    }
}
