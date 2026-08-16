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
///     Scope today is the read verbs. The full criterion — zero writes for every command except
///     <c>encryption</c> — is still red, because the write verbs write the settings table directly;
///     it goes green when WP7's transport is wired, and is the reason this seam exists now. The
///     command list is derived from the command tree, so a read verb added later is covered
///     without an edit here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CliBankWriteTests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-cli-writes");
    private ISqliteConnectionFactory _factory = null!;

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

        data.Add("settings noise entries", ["settings", "noise", "entries"]);
        data.Add("watch registered", ["watch", "registered"]);
        data.Add("extract prune", ["extract", "prune"]);
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
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
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
            ["--data-root", _dataRoot, .. argv], HardCap, TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"'{label}' failed; stderr: {run.Stderr}");

        var committed = await observer.HasCommittedSinceMarkAsync(TestContext.Current.CancellationToken);
        var after = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);
        committed.ShouldBeFalse(
            $"'{label}' committed a write to the bank; tables changed: {string.Join(", ", BankContent.Changed(before, after))}");
    }

    /// <summary>
    ///     The seam's own proof: a write verb through the same harness is seen. Without this, the
    ///     assertion above would be indistinguishable from one that can only pass.
    /// </summary>
    [Fact]
    public async Task AWriteCommand_IsSeen_ProvingTheAssertionCanFail()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await using var observer = await BankCommitObserver.OpenAsync(_factory, TestContext.Current.CancellationToken);

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "settings", "sweep", "threshold", "set", "0.42"],
            HardCap, TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"the writer must exit cleanly; stderr: {run.Stderr}");

        (await observer.HasCommittedSinceMarkAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }
}
