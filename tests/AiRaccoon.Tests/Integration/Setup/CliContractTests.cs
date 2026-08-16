using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     WP7-T7 (docs/plans/2026-08-16-bank-open-cost-implementation.md §6): the exit code, stdout
///     and stderr a script sees, per scenario. Recorded against the pre-WP6 CLI at 6f8347f3 and
///     replayed here unchanged apart from the argv — a thin caller is exactly where these regress
///     without anyone noticing.
///     <para />
///     The scenarios run in order against one data root: several read back what an earlier one
///     wrote, which is what makes this a contract rather than a set of independent smoke tests.
///     <para />
///     Two rows are owed and cannot be recorded yet, because no settings command talks to a
///     server. <see cref="OwedWhenTheTransportIsWired" /> names them, so they are visibly pending
///     rather than quietly absent; the transport half of each is already pinned by
///     <c>ServerSettingsStoreTests</c>, so what is missing is only the exit code and message a
///     script sees.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CliContractTests : IDisposable
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-cli-contract");

    /// <summary>One recorded scenario. Empty expected output means the stream must be empty.</summary>
    private sealed record Scenario(string[] Argv, int Exit, string Stdout, string Stderr);

    /// <summary>
    ///     The scenarios this table still owes. Each needs a settings command that goes through the
    ///     server, so neither can be recorded until the CLI composition root is wired; both must be
    ///     added — with a distinct exit code — before WP7 is done.
    /// </summary>
    public static readonly string[] OwedWhenTheTransportIsWired =
        ["a settings command whose server refuses the token", "a settings command with no server reachable"];

    private static readonly Scenario[] Recorded =
    [
        new(["settings", "sweep", "threshold", "set", "0.5"], 0, "sweep threshold set to 0.5", ""),
        new(["settings", "sweep", "threshold", "set", "5"], ExitCode.InvalidArgument, "",
            "ai-raccoon: invalid threshold '5' (expected a number in 0..1)"),
        new(["settings", "sweep", "show"], 0, "enabled: True  interval: 24 h  threshold: 0.5", ""),
        new(["settings", "access", "default", "set", "ro"], 0, "access default set to ro", ""),
        new(["settings", "access", "default", "set", "bogus"], ExitCode.InvalidArgument, "",
            "ai-raccoon: invalid access mode 'bogus' (expected ro, rw or full)"),
        new(["settings", "access", "list"], 0, "default: ro", ""),
        new(["settings", "queryguard", "enable"], 0, "query guard enabled", ""),
        new(["settings", "performance", "buffer-capacity", "99999999"], ExitCode.InvalidArgument, "",
            "ai-raccoon: buffer capacity must be at most 1000000 measurements"),
        new(["settings", "extract", "mode", "bogus"], ExitCode.InvalidArgument, "",
            "ai-raccoon: mode must be 'propose' or 'promote'"),
        new(["settings", "extract", "list"], 0, "enabled: False  mode: propose  interval: 30 min  queue-capacity: 1000", ""),
        new(["settings", "ingest", "scope", "list", "*"], 0, "", ""),
        new(["watch", "registered"], 0, "no registered watches", ""),
        new(["extract", "prune"], 0, "promotion queue: no orphaned candidates found", ""),
        new(["bogusverb"], ExitCode.FailedToParseCliArgs, "", "Unrecognized command or argument 'bogusverb'."),
        new(["settings", "sweep", "bogus"], ExitCode.InvalidArgument, "",
            "Required command was not provided.\nUnrecognized command or argument 'bogus'.")
    ];

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task EveryScenario_KeepsItsExitCodeAndOutput()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        foreach (var scenario in Recorded)
        {
            var label = string.Join(' ', scenario.Argv);
            var run = await RaccoonProcess.RunAsync(
                ["--data-root", _dataRoot, .. scenario.Argv], HardCap, TestContext.Current.CancellationToken);

            run.ExitCode.ShouldBe(scenario.Exit, $"'{label}' exit code; stdout: {run.Stdout} stderr: {run.Stderr}");
            Normalize(run.Stdout).ShouldBe(scenario.Stdout, $"'{label}' stdout");
            Normalize(run.Stderr).ShouldBe(scenario.Stderr, $"'{label}' stderr");
        }
    }

    private static string Normalize(string stream) => stream.ReplaceLineEndings("\n").Trim('\n');
}
