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
///     GH #371 hard constraint: a chunk-index repair must never run because a clock fired, only
///     because a human explicitly asked. That guard is NOT "never appear on the maintenance job
///     list" — ADR-0075's amendment puts <see cref="ChunkIndexRepairJob" /> on that list,
///     on-demand-only, mirroring <see cref="ModelMigrationJob" /> (ADR-0076): its
///     <see cref="IMaintenanceJob.Interval" /> is null, so <see cref="MaintenanceJobRunner" />/
///     <see cref="BankMaintenanceHostedService" /> — which can start jobs within seconds of the first
///     bank open — can only ever run it when <see cref="IMaintenanceJob.HasWorkAsync" /> finds a
///     repair_requests row `repair chunk-index --apply` committed through the server
///     (<see cref="ChunkIndexRepairJobTests" /> covers that gating directly). Two proofs here:
///     <see cref="ChunkIndexRepair" /> itself structurally cannot BE one of those jobs, and the real
///     DI-composed job list's entry for it is on-demand-only.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ChunkIndexRepairDoesNotAutoStartTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void ChunkIndexRepair_DoesNotImplementIMaintenanceJob() =>
        typeof(ChunkIndexRepair).GetInterfaces().ShouldNotContain(typeof(IMaintenanceJob),
            "a type wired into the auto-run job list must be an IMaintenanceJob — this one structurally cannot be added to it by accident; ChunkIndexRepairJob wraps it instead");

    [Fact]
    public void RegisterMemoryServices_TheBankOpenJobList_HasNoClockIntervalForChunkIndexRepair()
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
        var chunkIndexJobs = jobs.Where(job => job.Name.Contains("chunk-index", StringComparison.OrdinalIgnoreCase)).ToList();
        chunkIndexJobs.ShouldHaveSingleItem("exactly one on-demand relay for the CLI-requested chunk-index repair, mirroring ModelMigrationJob");
        chunkIndexJobs.Single().Interval.ShouldBeNull(
            "never clock-scheduled — HasWorkAsync (gated on a repair_requests row) is the only due-ness signal");
    }
}
