using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tests.Integration.Setup;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     The fixture's ledger pre-stamp (CliBankWriteTests.InitializeAsync) must cover exactly the
///     DI-registered maintenance jobs: a job registered without a stamped row is DUE
///     (MaintenanceJobRunner.IsDue is true while last_run_at is NULL), so every command's
///     auto-started server re-runs it inside the command's observed window on a starved runner —
///     the 2026-08-20 nightly's F1-F5. This cross-check makes a registry addition without a
///     fixture row fail PR CI (Fast) instead of the next nightly.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliBankWriteLedgerDriftTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void DiRegisteredJobs_MatchTheFixtureLedgerStampList()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Scope = InstallScope.User
        }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Stdio));

        using var provider = services.BuildServiceProvider();

        var registered = provider.GetRequiredService<IReadOnlyList<IMaintenanceJob>>()
            .Select(job => job.Name).Order(StringComparer.Ordinal).ToArray();
        var stamped = CliBankWriteTests.MaintenanceLedgerNames
            .Order(StringComparer.Ordinal).ToArray();

        stamped.ShouldBe(registered,
            "the fixture stamps exactly the DI-registered jobs — an un-stamped job's startup pass writes inside the command window (2026-08-20 nightly F1-F5)");
    }
}
