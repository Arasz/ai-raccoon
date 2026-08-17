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
///     GH #371 hard constraint: the chunk-index repair must never run unattended against a live
///     bank — <see cref="MaintenanceJobRunner" />/<see cref="BankMaintenanceHostedService" /> can
///     start jobs within seconds of the first bank open, so it must not be reachable through that
///     list. Two independent proofs: it cannot structurally BE one of those jobs, and the real
///     DI-composed job list — the actual bank-open trigger surface — does not contain one.
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
            "a type wired into the auto-run job list must be an IMaintenanceJob — this one structurally cannot be added to it by accident");

    [Fact]
    public void RegisterMemoryServices_TheBankOpenJobList_DoesNotIncludeTheChunkIndexRepair()
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
        jobs.ShouldAllBe(job => !job.Name.Contains("chunk-index", StringComparison.OrdinalIgnoreCase)
                                 && !job.DisplayName.Contains("chunk-index", StringComparison.OrdinalIgnoreCase));
    }
}
