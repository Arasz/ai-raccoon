using System.Globalization;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     WP7-T1 (docs/plans/2026-08-16-bank-open-cost-implementation.md §6): a CLI process must not
///     write the bank. Each command runs as a real <c>ai-raccoon</c> OS process against a scratch
///     data root, with a connection held open across it watching for a committed write transaction
///     — INSERT, UPDATE, DELETE and DDL alike, which is the criterion as the plan states it.
///     <para />
///     The precondition is an existing, current bank: creating one legitimately writes it. The
///     fixture opens it once so every command below meets a bank that is already stamped.
///     <para />
///     Scope here is the read verbs, and it stays that way even with the transport wired
///     (ADR-0075 §5.3): a write verb still has to end in a committed write <em>somewhere</em> on the
///     same bank file, and <see cref="BankCommitObserver" /> watches the file, not the process that
///     touched it — it cannot tell "the CLI wrote directly" from "the CLI's HTTP call made the
///     server write", so it is not the seam that proves write-exclusivity for write verbs.
///     <see cref="AWriteCommand_IsSeen_ProvingTheAssertionCanFail" /> below still shows a write
///     verb committing — now via the auto-started server rather than the CLI itself — which is
///     what keeps <see cref="ReadCommand_CommitsNothingToTheBank" /> a real assertion rather than one
///     that could only ever pass. The actual "CLI never writes directly" guarantee for write verbs is
///     proved elsewhere, by construction: <c>CliWriteOptOutsTests</c> pins the opt-out list,
///     <c>AppRunnerSettingsRoutingTests</c> pins that a non-opted-out command never resolves
///     <c>SqliteSettingsStore</c>, and <c>ConfigCommands</c> never takes <c>ISqliteConnectionFactory</c>
///     for any settings verb.
///     <para />
///     The command list is derived from the command tree, so a read verb added later is covered
///     without an edit here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CliBankWriteTests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-cli-writes");
    private ISqliteConnectionFactory _factory = null!;
    private LoopbackPort _portLease = null!;
    private int _port;

    /// <summary>Commands that only report; none of them has any business writing the bank.</summary>
    public static TheoryData<string, string[]> ReadCommands()
    {
        var data = new TheoryData<string, string[]>();
        foreach (var (path, argv) in SettingsCommandTreeTests.SettingsLeaves)
        {
            if (path.EndsWith(" show", StringComparison.Ordinal) || path.EndsWith(" list", StringComparison.Ordinal))
            {
                data.Add(path, argv);
            }
        }

        data.Add("noise entries", ["noise", "entries"]);
        data.Add("watch registered", ["watch", "registered"]);
        data.Add("extract prune", ["extract", "prune"]);
        data.Add("repair chunk-index", ["repair", "chunk-index"]);
        data.Add("repair reingest", ["repair", "reingest"]);
        return data;
    }

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));

        // Creating the bank is itself a write; do it here so the assertions below meet a bank that
        // is already stamped and current.
        await using var _ = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        _portLease = LoopbackPort.Reserve();
        _port = _portLease.Port;
        _portLease.ReleaseForBind();

        // Warm-up: every settings command now auto-starts a full server (ADR-0075 §5.1), and
        // BankMaintenanceHostedService runs an unrelated startup checkpoint pass the instant it
        // does (BankMaintenanceHostedService.cs:73), racily, in the background. Absorbing that here
        // and waiting for it to settle keeps it from being mistaken for a write the verb under test
        // made — the assertions below only care about what the verb itself commits.
        var warmup = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), "settings", "sweep", "show"],
            HardCap, TestContext.Current.CancellationToken);
        warmup.ExitCode.ShouldBe(0, $"warm-up failed; stderr: {warmup.Stderr}");
        await WaitForBankToSettleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Polls until two consecutive snapshots match, so the startup maintenance pass's own
    /// write (racy relative to the server answering its first request) is not still in flight.</summary>
    private async Task WaitForBankToSettleAsync(CancellationToken cancellationToken)
    {
        var previous = await BankContent.SnapshotAsync(_factory, cancellationToken);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            var current = await BankContent.SnapshotAsync(_factory, cancellationToken);
            if (BankContent.Changed(previous, current).Count == 0)
            {
                return;
            }

            previous = current;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, _port, CancellationToken.None);
        TestData.DeleteTempRoot(_dataRoot);
    }

    [Theory]
    [MemberData(nameof(ReadCommands))]
    public async Task ReadCommand_CommitsNothingToTheBank(string label, string[] argv)
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await using var observer = await BankCommitObserver.OpenAsync(_factory, TestContext.Current.CancellationToken);
        var before = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), .. argv],
            HardCap, TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"'{label}' failed; stderr: {run.Stderr}");

        var committed = await observer.HasCommittedSinceMarkAsync(TestContext.Current.CancellationToken);
        var after = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);
        committed.ShouldBeFalse(
            $"'{label}' committed a write to the bank; tables changed: {string.Join(", ", BankContent.Changed(before, after))}");
    }

    /// <summary>
    ///     The seam's own proof: a write verb through the same harness is seen. Without this, the
    ///     assertion above would be indistinguishable from one that can only pass. Under
    ///     write-exclusivity the commit still happens — now performed by the server this write verb
    ///     auto-starts, not by the CLI process itself, but a committed write to the same file either way.
    /// </summary>
    [Fact]
    public async Task AWriteCommand_IsSeen_ProvingTheAssertionCanFail()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await using var observer = await BankCommitObserver.OpenAsync(_factory, TestContext.Current.CancellationToken);

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture),
                "settings", "sweep", "threshold", "set", "0.42"],
            HardCap, TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"the writer must exit cleanly; stderr: {run.Stderr}");

        (await observer.HasCommittedSinceMarkAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }
}
