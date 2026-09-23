using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     WP7-T7 (docs/plans/2026-08-16-bank-open-cost-implementation.md §6): the exit code, stdout
///     and stderr a script sees, per scenario. Recorded against the pre-WP6 CLI at 6f8347f3 and
///     replayed here unchanged apart from the argv — a thin caller is exactly where these regress
///     without anyone noticing.
///     <para />
///     The scenarios run in order against one data root and one auto-started server (ADR-0075
///     §5.1/§5.3): several read back what an earlier one wrote, which is what makes this a contract
///     rather than a set of independent smoke tests. An explicit <c>--port</c> keeps every scenario
///     off the environment's default (7721) and off every other test's port.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class CliContractTests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-cli-contract");
    private LoopbackPort _portLease = null!;
    private int _port;

    /// <summary>One recorded scenario. Exit is the literal number a script sees (ADR-0107), not a constant, so a
    /// renumbering shows up here. Empty expected output means the stream must be empty. Stderr
    /// may contain the literal <c>{PORT}</c>, substituted with the instance's own port before comparing.</summary>
    private sealed record Scenario(string[] Argv, int Exit, string Stdout, string Stderr);

    /// <summary>The N1 shared-backend disclosure (EventId 687) every settings-routed acquire emits —
    /// on the cold start and on reuse alike: the backend outlives this command under its own idle
    /// timeout. Present in every scenario that reaches the server, absent from the ones rejected
    /// before the acquire (argument and parse errors).</summary>
    private const string BackendOutlivesCommand =
        "info: AiRaccoon.Settings.CliSettingsBackend[687]\n      ai-raccoon: the backend on port {PORT} keeps running after this command exits, under its own idle timeout — stop it with ai-raccoon serve --restart --port {PORT}";

    private static readonly Scenario[] Recorded =
    [
        // The cold scenario: no server exists yet for this data root, so this one command pays the
        // auto-start (ADR-0075 §5.1) and is the only one that logs the 633 starting line — every
        // scenario after this reuses the same live server and carries only the 687 disclosure.
        new(["settings", "sweep", "threshold", "set", "0.5"], 0, "sweep threshold set to 0.5",
            "info: AiRaccoon.Hosting.Proxy.BackendLauncher[633]\n      ai-raccoon: starting the backend on port {PORT}\n" +
            BackendOutlivesCommand),
        new(["settings", "sweep", "threshold", "set", "5"], 10, "",
            "ai-raccoon: invalid threshold '5' (expected a number in 0..1)"),
        new(["settings", "sweep", "show"], 0, "enabled: True  interval: 24 h  threshold: 0.5", BackendOutlivesCommand),
        new(["settings", "access", "default", "set", "ro"], 0, "access default set to ro", BackendOutlivesCommand),
        new(["settings", "access", "default", "set", "bogus"], 10, "",
            "ai-raccoon: invalid access mode 'bogus' (expected ro, rw or full)"),
        new(["settings", "access", "list"], 0, "default: ro", BackendOutlivesCommand),
        new(["settings", "queryguard", "enable"], 0, "query guard enabled", BackendOutlivesCommand),
        new(["settings", "performance", "buffer-capacity", "99999999"], 10, "",
            "ai-raccoon: buffer capacity must be at most 1000000 measurements"),
        new(["settings", "extract", "mode", "bogus"], 10, "",
            "ai-raccoon: mode must be 'propose' or 'promote'"),
        new(["settings", "extract", "list"], 0, "enabled: False  mode: propose  interval: 30 min  queue-capacity: 1000  auto-promote-threshold: off", BackendOutlivesCommand),
        new(["settings", "ingest", "scope", "list", "*"], 0, "", BackendOutlivesCommand),
        new(["watch", "registered"], 0, "no registered watches", BackendOutlivesCommand),
        new(["extract", "prune"], 0, "promotion queue: no orphaned candidates found", BackendOutlivesCommand),
        new(["bogusverb"], 11, "", "Unrecognized command or argument 'bogusverb'."),
        // A recognised-but-incomplete command shows help for the command it got as far as
        // (docs/adr/0060 keeps this distinct from the bogusverb row above: that one never
        // resolves a command path, so it gets no help — only its error).
        new(["settings", "sweep", "bogus"], 11, "",
            "Required command was not provided.\nUnrecognized command or argument 'bogus'.\n\n" +
            "Description:\n" +
            "  Background reaper configuration: the kill switch, the cadence and the rating threshold it deletes below. The reaper is ON by default — 'sweep disable' is how you disarm it. Per-entry TTLs are data, set by the memory_set_ttl tool, not configured here.\n\n" +
            "Usage:\n" +
            "  AiRaccoon settings sweep [command] [options]\n\n" +
            "Options:\n" +
            "  -?, -h, --help  Show help and usage information\n\n" +
            "Commands:\n" +
            "  enable                    Arms the background reaper (the default: it deletes expired entries on its cadence)\n" +
            "  disable                   Disarms the background reaper — nothing is deleted until it is enabled again\n" +
            "  interval-hours <1..8760>  Sets the reaper cadence in hours (1..8760, default 24); applies live, no server restart needed\n" +
            "  threshold                 Sweep rating threshold\n" +
            "  list, show                Shows the whole policy: enabled, interval hours and threshold (row values, else the defaults)"),
        new(["model", "embedding", "set", "openai"], 12, "",
            "Required argument missing for command: 'openai'.\n\n" +
            "Description:\n" +
            "  Routes through an OpenAI-compatible endpoint; key via --api-key (persisted in settings)\n\n" +
            "Usage:\n" +
            "  AiRaccoon model embedding set openai <model> [<base-url>] [options]\n\n" +
            "Arguments:\n" +
            "  <model-id>\n" +
            "  <url>\n\n" +
            "Options:\n" +
            "  --api-key <key>  API key persisted in the settings table\n" +
            "  --dims <n>       Output dimension the endpoint returns (sqlite-vec cannot infer it)\n" +
            "  -?, -h, --help   Show help and usage information")
    ];

    public async ValueTask InitializeAsync()
    {
        _portLease = LoopbackPort.Reserve();
        _port = _portLease.Port;
        _portLease.ReleaseForBind();
        // F39: the settings acquire refuses an empty non-default root before it probes or spawns, and
        // the cold scenario below is exactly that acquire — so the fixture root holds a real bank.
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, _port, CancellationToken.None);
        TestData.DeleteTempRoot(_dataRoot);
    }

    [RetryFact]
    public async Task EveryScenario_KeepsItsExitCodeAndOutput()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        foreach (var scenario in Recorded)
        {
            var label = string.Join(' ', scenario.Argv);
            var run = await RunAsync(_dataRoot, _port, scenario.Argv);

            run.ExitCode.ShouldBe(scenario.Exit, $"'{label}' exit code; stdout: {run.Stdout} stderr: {run.Stderr}");
            Normalize(run.Stdout).ShouldBe(scenario.Stdout, $"'{label}' stdout");
            Normalize(run.Stderr).ShouldBe(
                scenario.Stderr.Replace("{PORT}", _port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                $"'{label}' stderr");
        }
    }

    /// <summary>
    ///     The hanging-holder row, re-shaped for attach-or-start (ADR-0106): a listener that takes
    ///     the connection and says nothing cannot be proven, so the command completes on a bounded
    ///     private fallback and says so — the held port and the remedy. Before the revert this was
    ///     exit 18 (no server reachable); the fallback is now the contract.
    /// </summary>
    [RetryFact]
    public async Task SettingsCommand_WithAHangingHolder_FallsBackPrivately_AndWarns()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        var dataRoot = TestData.CreateTempRoot("ai-raccoon-cli-contract-hanging");
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
        using var foreign = LoopbackPort.Occupy();

        var fallbackPort = 0;
        try
        {
            var run = await RunAsync(dataRoot, foreign.Port, ["settings", "sweep", "show"]);

            run.ExitCode.ShouldBe(0, $"the private fallback must serve the command; stderr: {run.Stderr}");
            run.Stderr.ShouldContain($"listener on port {foreign.Port} did not prove");
            run.Stderr.ShouldContain("stop the listener");
            // The N1 disclosure names the fallback's own port; parse it so this test stops the child.
            var match = Regex.Match(run.Stderr, @"backend on port (\d+) keeps running");
            match.Success.ShouldBeTrue($"the disclosure must name the fallback port; stderr: {run.Stderr}");
            fallbackPort = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (fallbackPort != 0)
            {
                await RaccoonBackendCleanup.ShutdownIfRunningAsync(dataRoot, fallbackPort, CancellationToken.None);
            }

            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     WP7-T7's server-refused row: a live server is already answering on the port, but the
    ///     token file it minted has been tampered with — a well-formed, wrong-length-safe token that
    ///     is simply not the one the running server issued.
    /// </summary>
    [RetryFact]
    public async Task SettingsCommand_WhenTheServerRefusesTheToken_ExitsDistinctly()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        var dataRoot = TestData.CreateTempRoot("ai-raccoon-cli-contract-refused");
        // F39: the seed's auto-launch refuses an empty non-default root, so it starts from a bank.
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();

        try
        {
            // Auto-starts the server for this data root/port and mints its token.
            var seed = await RunAsync(dataRoot, port, ["settings", "sweep", "show"]);
            seed.ExitCode.ShouldBe(0, $"seed failed; stderr: {seed.Stderr}");

            var tokenFile = new McpTokenFile(dataRoot);
            var minted = tokenFile.Read();
            minted.ShouldNotBeNull("the seed run must have minted a token");
            await File.WriteAllTextAsync(tokenFile.Path, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
                TestContext.Current.CancellationToken);

            var run = await RunAsync(dataRoot, port, ["settings", "sweep", "show"]);

            run.ExitCode.ShouldBe(ErrorCode.Server.RequestTokenRefused);
            run.Stdout.ShouldBeEmpty();
            run.Stderr.ShouldContain("refused");

            // Restore the real token so the server can be asked to stop cleanly.
            await File.WriteAllTextAsync(tokenFile.Path, minted, TestContext.Current.CancellationToken);
        }
        finally
        {
            await RaccoonBackendCleanup.ShutdownIfRunningAsync(dataRoot, port, TestContext.Current.CancellationToken);
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>Root args shared by every process this fixture runs. The attach-or-start default
    /// (ADR-0106) is the shared-server shape the recorded contract pins: one server is started on
    /// the configured port and every scenario after it attaches by proof — no flag needed.</summary>
    private static Task<ProcessRun> RunAsync(string dataRoot, int port, string[] argv) =>
        RaccoonProcess.RunAsync(
            ["--data-root", dataRoot, "--port", port.ToString(CultureInfo.InvariantCulture), .. argv],
            HardCap, TestContext.Current.CancellationToken);

    private static string Normalize(string stream) => stream.ReplaceLineEndings("\n").Trim('\n');
}
