using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     Air-merge P2: the project-ids repair obeys the same hard constraint as the chunk-index and
///     reingest repairs — it must never run because a clock fired, only because a human explicitly
///     asked via `repair project-ids --apply`. <see cref="ProjectIdsRepairJob" /> rides the
///     maintenance job list on-demand-only (<see cref="IMaintenanceJob.Interval" /> is null);
///     <see cref="ProjectIdsRepair" /> itself structurally cannot BE one of those jobs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsRepairDoesNotAutoStartTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void ProjectIdsRepair_DoesNotImplementIMaintenanceJob() =>
        typeof(ProjectIdsRepair).GetInterfaces().ShouldNotContain(typeof(IMaintenanceJob),
            "a type wired into the auto-run job list must be an IMaintenanceJob — this one structurally cannot be added to it by accident; ProjectIdsRepairJob wraps it instead");

    [Fact]
    public void RegisterMemoryServices_TheBankOpenJobList_HasNoClockIntervalForProjectIdsRepair()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Stdio));

        using var provider = services.BuildServiceProvider();

        var jobs = provider.GetRequiredService<IReadOnlyList<IMaintenanceJob>>().ToList();
        var projectIdsJobs = jobs.Where(job => job.Name.Contains("project-ids", StringComparison.OrdinalIgnoreCase)).ToList();
        projectIdsJobs.ShouldHaveSingleItem("exactly one on-demand relay for the CLI-requested project-ids repair, mirroring the other two repair jobs");
        projectIdsJobs.Single().Interval.ShouldBeNull(
            "never clock-scheduled — HasWorkAsync (gated on a repair_requests row) is the only due-ness signal");
        jobs.IndexOf(projectIdsJobs.Single()).ShouldBeLessThan(
            jobs.FindIndex(job => job.Name.Contains("pending-embed", StringComparison.OrdinalIgnoreCase)),
            "the fold leaves renamed rows pending, so it must run ahead of PendingEmbedJob in the same pass");
    }
}
