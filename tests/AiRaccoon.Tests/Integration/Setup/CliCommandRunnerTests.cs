using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     AppRunner: the one-shot config-verb path shares the server's bank resolution
///     (--data-root/--install-scope) and wires the real bank, watch store, and encryption resolver.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliCommandRunnerTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-config-verb-runner");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private async Task<(int Exit, string Out, string Err, ServerConfig Config)> Run(string[] args, bool expectParseErrors = false)
    {
        CliArgs.TryParse(args, out var parsed);
        if (!expectParseErrors)
        {
            parsed!.Errors.ShouldBeEmpty();
        }

        var config = parsed!.Options.ToServerConfig();

        // Serialized with the encryption tests: AIRACCOON_DB_PASSPHRASE is process-global and
        // must be cleared during a run so a dev machine's value cannot poison a fresh-bank test.
        // The Console redirect stays inside the gate: a prior holder's restore must not run
        // between our SetOut and the AppRunner's stream capture.
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        var exit = 0;
        var (stdout, stderr) = await ConsoleCapture.RunAsync(async () => exit = await new AppRunner().Run(args));
        return (exit, stdout, stderr, config);
    }

    [Fact]
    public async Task AccessDefaultShow_ReturnsConfigCommandsExitCode()
    {
        var (exit, stdout, _, _) = await Run(["--data-root", _dataRoot, "access", "default", "show"]);

        exit.ShouldBe(0);
        stdout.ShouldContain("rw");
    }

    [Fact]
    public async Task VerbError_ReturnsInvalidArgument_NotTheEncryptionKeyExitCode()
    {
        var (exit, _, stderr, _) = await Run(["--data-root", _dataRoot, "access", "default", "set", "bogus"]);

        exit.ShouldBe(ExitCode.InvalidArgument);
        exit.ShouldNotBe(ExitCode.FailedToResolveEncryptionKey);
        stderr.ShouldContain("invalid access mode");
    }

    [Fact]
    public async Task UserScope_WritesBankUnderDataRoot()
    {
        var (exit, _, _, config) = await Run(["--data-root", _dataRoot, "access", "default", "set", "ro"]);

        exit.ShouldBe(0);
        config.Options.Scope.ShouldBe(InstallScope.User);
        File.Exists(Path.Combine(_dataRoot, "memory.db")).ShouldBeTrue();
    }

    [Fact]
    public async Task ProjectScope_WritesBankUnderDotAiRaccoon()
    {
        var (exit, _, _, config) = await Run(
            ["--data-root", _dataRoot, "--install-scope", "project", "access", "default", "set", "ro"]);

        exit.ShouldBe(0);
        config.Options.Scope.ShouldBe(InstallScope.Project);
        File.Exists(Path.Combine(_dataRoot, ".ai-raccoon", "memory.db")).ShouldBeTrue();
    }

    /// <summary>UX-F4: a missing required argument must be reported once — not once from
    /// System.CommandLine's own error rendering, once from CliRendering's own loop, and once
    /// more from ConfigCommands reformatting the exception thrown when it dispatches anyway.</summary>
    [Fact]
    public async Task MissingArgument_PrintsTheErrorExactlyOnce()
    {
        var (exit, _, stderr, _) = await Run(["--data-root", _dataRoot, "access", "set"], expectParseErrors: true);

        exit.ShouldBe(ExitCode.InvalidArgument);
        CountOccurrences(stderr, "Required argument missing for command: 'set'.").ShouldBe(1);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task WatchRegistered_WiresWatchStoreFromBank()
    {
        var (exit, stdout, _, _) = await Run(["--data-root", _dataRoot, "watch", "registered"]);

        exit.ShouldBe(0);
        stdout.ShouldContain("no registered watches");
    }

    [Fact]
    public async Task EncryptionShow_ResolvesKeyThroughSidecar()
    {
        var (exit, stdout, _, _) = await Run(["--data-root", _dataRoot, "encryption", "show"]);

        exit.ShouldBe(0);
        stdout.ShouldContain("source: env");
    }

    /// <summary>QA-1: --version must exit after rendering, not fall through to the proxy
    /// (which attaches to, or spawns, a server on the machine).</summary>
    [Fact]
    public async Task Version_ExitsWithoutLaunchingTheProxy()
    {
        var (exit, stdout, stderr, _) = await Run(["--version"]);

        exit.ShouldBe(0);
        stdout.ShouldBeEmpty();
        stderr.ShouldMatch(@"^\d+\.\d+\.\d+");
    }

    /// <summary>QA-5: encryption unset on an env-keyed bank with no passphrase must take the
    /// documented warning path; the DI-composed command must not hand the Bitwarden provider
    /// an env-source EncryptionData (Guard: "encryptionData.SecretId must not be null").</summary>
    [Fact]
    public async Task EncryptionUnset_EnvKeyedBankNoPassphrase_WarnsAndExitsKeyResolutionFailure()
    {
        // Create the bank first: on a missing bank, unset takes the clean-reset path (exit 0).
        var (seedExit, _, _, _) = await Run(["--data-root", _dataRoot, "access", "default", "show"]);
        seedExit.ShouldBe(0);

        var (exit, _, stderr, _) = await Run(["--data-root", _dataRoot, "encryption", "unset"]);

        exit.ShouldBe(ExitCode.FailedToResolveEncryptionKey);
        stderr.ShouldContain("no AIRACCOON_DB_PASSPHRASE set");
        stderr.ShouldNotContain("must not be null");
    }

    /// <summary>QA-1: verb-level --help must exit 0 after rendering; it must not run the
    /// dispatcher on the bare verb path (which errored "unhandled command: sync").</summary>
    [Fact]
    public async Task VerbHelp_ExitsZero_WithoutRunningTheVerb()
    {
        var (exit, _, stderr, _) = await Run(["sync", "--help"]);

        exit.ShouldBe(0);
        stderr.ShouldNotContain("unhandled command");
    }
}
