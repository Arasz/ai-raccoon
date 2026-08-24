using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Settings;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     WP7 (ADR-0075 §5.3): AppRunner.RunCliCommand routes ISettingsStore per command path — the
///     opt-out list (<see cref="CliWriteOptOuts" />) stays on the direct bank-backed store; every
///     other command is bound to whatever the injected acquire function returns. A fake acquire
///     function keeps this fast — the real acquire path (BackendLauncher, a live server) is covered
///     by CliContractTests/CliBankWriteTests.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class AppRunnerSettingsRoutingTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-settings-routing");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ANonOptedOutCommand_UsesTheInjectedAcquireFunction()
    {
        var acquireCalls = 0;
        var fake = new InMemorySettings();
        fake.Values[SweepConfigKeys.EnabledGlobal] = "false";

        var (exit, stdout) = await Run(
            (_, _, _) =>
            {
                acquireCalls++;
                return Task.FromResult<ISettingsStore>(fake);
            },
            ["--data-root", _dataRoot, "settings", "sweep", "show"]);

        exit.ShouldBe(0);
        acquireCalls.ShouldBe(1);
        stdout.ShouldContain("enabled: False");
    }

    [Fact]
    public async Task AnEncryptionCommand_NeverCallsTheAcquireFunction()
    {
        var acquireCalls = 0;

        var (exit, _) = await Run(
            (_, _, _) =>
            {
                acquireCalls++;
                return Task.FromResult<ISettingsStore>(new InMemorySettings());
            },
            ["--data-root", _dataRoot, "encryption", "show"]);

        exit.ShouldBe(0);
        acquireCalls.ShouldBe(0);
    }

    /// <summary>
    ///     ADR-0076 moved with the rule: `model set` used to be the one command asserted to never
    ///     call acquire (CliWriteOptOuts). Now it is routed exactly like every other settings
    ///     command, so this is the mirror of ANonOptedOutCommand_UsesTheInjectedAcquireFunction, not
    ///     a deletion of the old assertion.
    /// </summary>
    [Fact]
    public async Task AModelSetCommand_UsesTheInjectedAcquireFunction()
    {
        var acquireCalls = 0;
        var fake = new InMemorySettings();

        var (exit, _) = await Run(
            (_, _, _) =>
            {
                acquireCalls++;
                return Task.FromResult<ISettingsStore>(fake);
            },
            ["--data-root", _dataRoot, "model", "embedding", "set", "local"]);

        exit.ShouldBe(0);
        acquireCalls.ShouldBe(1);
    }

    /// <summary>
    ///     ADR-0075 amendment: `repair &lt;verb&gt; --apply` used to write the bank directly from the
    ///     CLI process (IMemoryStore bound unconditionally, CliWriteOptOuts never named it) — the
    ///     defect this task fixes. It now routes through the same acquired IRepairStore as every
    ///     other non-opted-out command, exactly mirroring AModelSetCommand_UsesTheInjectedAcquireFunction.
    /// </summary>
    [Fact]
    public async Task ARepairCommand_UsesTheInjectedAcquireFunction()
    {
        var acquireCalls = 0;
        var fake = new InMemorySettings();

        var (exit, stdout) = await Run(
            (_, _, _) =>
            {
                acquireCalls++;
                return Task.FromResult<ISettingsStore>(fake);
            },
            ["--data-root", _dataRoot, "repair", "reingest"]);

        exit.ShouldBe(0);
        acquireCalls.ShouldBe(1);
        stdout.ShouldContain("reingest repair:");
    }

    /// <summary>
    ///     ADR-0075 amendment: `extract prune` used to write the bank directly from the CLI process
    ///     (IPromotionQueueStore bound unconditionally, never routed through CliWriteOptOuts's
    ///     mechanism at all) — the defect this task fixes. It now routes through the same acquired
    ///     IPromotionQueuePruneStore as every other non-opted-out command.
    /// </summary>
    [Fact]
    public async Task AnExtractPruneCommand_UsesTheInjectedAcquireFunction()
    {
        var acquireCalls = 0;
        var fake = new InMemorySettings();

        var (exit, stdout) = await Run(
            (_, _, _) =>
            {
                acquireCalls++;
                return Task.FromResult<ISettingsStore>(fake);
            },
            ["--data-root", _dataRoot, "extract", "prune"]);

        exit.ShouldBe(0);
        acquireCalls.ShouldBe(1);
        stdout.ShouldContain("no orphaned candidates found");
    }

    /// <summary>
    ///     ADR-0075 amendment: `settings maintenance list` used to open the bank directly from the
    ///     CLI process to read pragma stats, unconditionally, on every invocation. It now routes
    ///     through the same acquired IMaintenanceStatsStore as every other non-opted-out command.
    /// </summary>
    [Fact]
    public async Task ASettingsMaintenanceListCommand_UsesTheInjectedAcquireFunction()
    {
        var acquireCalls = 0;
        var fake = new InMemorySettings();

        var (exit, stdout) = await Run(
            (_, _, _) =>
            {
                acquireCalls++;
                return Task.FromResult<ISettingsStore>(fake);
            },
            ["--data-root", _dataRoot, "settings", "maintenance", "list"]);

        exit.ShouldBe(0);
        acquireCalls.ShouldBe(1);
        stdout.ShouldContain("db file:");
    }

    /// <summary>
    ///     ADR-0075 amendment: `noise entries` used to summarize noise_entries by opening the bank
    ///     directly from the CLI process (INoiseEntryStore bound unconditionally, never routed
    ///     through CliWriteOptOuts's mechanism at all) — the defect this task fixes. It now routes
    ///     through the same acquired INoiseSummaryStore as every other non-opted-out command.
    /// </summary>
    [Fact]
    public async Task ANoiseEntriesCommand_UsesTheInjectedAcquireFunction()
    {
        var acquireCalls = 0;
        var fake = new InMemorySettings();

        var (exit, stdout) = await Run(
            (_, _, _) =>
            {
                acquireCalls++;
                return Task.FromResult<ISettingsStore>(fake);
            },
            ["--data-root", _dataRoot, "noise", "entries"]);

        exit.ShouldBe(0);
        acquireCalls.ShouldBe(1);
        stdout.ShouldContain("total: 0");
    }

    /// <summary>
    ///     ADR-0075 amendment: `watch registered` used to list watch registrations by opening the
    ///     bank directly from the CLI process (IWatchStore bound unconditionally, never routed
    ///     through CliWriteOptOuts's mechanism at all) — the defect this task fixes. It now routes
    ///     through the same acquired IWatchRegisteredStore as every other non-opted-out command.
    /// </summary>
    [Fact]
    public async Task AWatchRegisteredCommand_UsesTheInjectedAcquireFunction()
    {
        var acquireCalls = 0;
        var fake = new InMemorySettings();

        var (exit, stdout) = await Run(
            (_, _, _) =>
            {
                acquireCalls++;
                return Task.FromResult<ISettingsStore>(fake);
            },
            ["--data-root", _dataRoot, "watch", "registered"]);

        exit.ShouldBe(0);
        acquireCalls.ShouldBe(1);
        stdout.ShouldContain("no registered watches");
    }

    /// <summary>A failed acquire surfaces as the command's own failure, not a crash.</summary>
    [Fact]
    public async Task WhenTheAcquireFunctionFails_TheCommandReportsItAndDoesNotThrow()
    {
        var (exit, _) = await Run(
            (_, _, _) => Task.FromException<ISettingsStore>(
                new SettingsServerUnavailableException("ai-raccoon: no settings server answered")),
            ["--data-root", _dataRoot, "settings", "sweep", "show"]);

        exit.ShouldBe(ExitCode.SettingsServerUnavailable);
    }

    private static async Task<(int Exit, string Stdout)> Run(
        Func<ServerConfig, ILoggerFactory, CancellationToken, Task<ISettingsStore>> acquireServerSettingsStore,
        string[] args)
    {
        await TestData.EnvVarGate.WaitAsync();
        try
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
            try
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
                // AppRunner captures Console.Out/Error at construction, so it must be built after
                // the redirection above — matching CliCommandRunnerTests' ConsoleCapture pattern.
                var exit = await new AppRunner(acquireServerSettingsStore).Run(args);
                return (exit, outWriter.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
        finally
        {
            TestData.EnvVarGate.Release();
        }
    }
}
