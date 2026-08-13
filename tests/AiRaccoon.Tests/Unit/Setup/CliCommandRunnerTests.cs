using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     AppRunner: the one-shot config-verb path shares the server's bank resolution
///     (--data-root/--install-scope) and wires the real bank, watch store, and encryption resolver.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliCommandRunnerTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-config-verb-runner");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private async Task<(int Exit, string Out, string Err, ServerConfig Config)> Run(string[] args)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        parsed!.CommandPath.ShouldNotBeEmpty();

        var config = parsed!.Options.ToServerConfig();

        // Serialized with the encryption tests: AIRACCOON_DB_PASSPHRASE is process-global and
        // must be cleared during a run so a dev machine's value cannot poison a fresh-bank test.
        // The Console redirect lives inside the gate too: a prior holder's restore must not run
        // between our SetOut and the AppRunner's stream capture.
        await TestData.EnvVarGate.WaitAsync();
        try
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
            try
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
                var exit = await new AppRunner().Run(args);
                return (exit, stdout.ToString(), stderr.ToString(), config);
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

    [Fact]
    public async Task AccessDefaultShow_ReturnsConfigCommandsExitCode()
    {
        var (exit, stdout, _, _) = await Run(["--data-root", _dataRoot, "access", "default", "show"]);

        exit.ShouldBe(0);
        stdout.ShouldContain("rw");
    }

    [Fact]
    public async Task VerbError_ReturnsNonZeroAndWritesStderr()
    {
        var (exit, _, stderr, _) = await Run(["--data-root", _dataRoot, "access", "default", "set", "bogus"]);

        exit.ShouldBe(1);
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
}
