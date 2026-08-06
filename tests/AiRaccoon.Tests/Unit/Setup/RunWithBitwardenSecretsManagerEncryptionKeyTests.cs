using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     ConfigVerbRunner: the one-shot config-verb path shares the server's bank resolution
///     (--data-root/--install-scope) and wires the real bank, watch store, and encryption resolver.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RunWithBitwardenSecretsManagerEncryptionKeyTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-config-verb-runner");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private async Task<(int Exit, string Out, string Err, ServerConfig Config)> Run(string[] args)
    {
        var parsed = CliArgs.TryParse(args);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var config = ServerConfig.Build(parsed.Options);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // Serialized with the encryption tests: AIRACCOON_DB_PASSPHRASE is process-global and
        // must be cleared during a run so a dev machine's value cannot poison a fresh-bank test.
        await TestData.EnvVarGate.WaitAsync();
        var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
            var exit = await RunWithBitwardenSecretsManagerEncryptionKey.RunAsync(parsed, config, stdout, stderr, TextReader.Null,
                TestContext.Current.CancellationToken);
            return (exit, stdout.ToString(), stderr.ToString(), config);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
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
