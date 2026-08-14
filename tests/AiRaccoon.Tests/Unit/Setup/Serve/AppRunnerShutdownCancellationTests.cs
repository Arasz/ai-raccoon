using System.Runtime.InteropServices;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     AppRunner (.NET-F3): the one-shot CLI-command and stdio-proxy paths get no host shutdown
///     pipeline, so SIGINT/SIGTERM must be wired by hand onto Token cancellation.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class AppRunnerShutdownCancellationTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-shutdown-cancellation");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     Invokes the signal handler directly instead of sending a real OS signal: a real SIGINT/SIGTERM
    ///     affects the whole test process and cannot be sent cleanly from inside a single xUnit test.
    /// </summary>
    [Fact]
    public void OnPosixSignal_CancelsToken_AndSuppressesDefaultTermination()
    {
        var runner = new AppRunner();
        var context = new PosixSignalContext(PosixSignal.SIGINT);

        runner.OnPosixSignal(context);

        runner.Token.IsCancellationRequested.ShouldBeTrue();
        context.Cancel.ShouldBeTrue();
    }

    [Fact]
    public async Task RunCliCommand_RegistersShutdownCancellation()
    {
        var (exit, runner) = await Run(["--data-root", _dataRoot, "access", "default", "show"]);

        exit.ShouldBe(0);
        runner.ShutdownCancellationRegistrations.ShouldBe(1);
    }

    // Mirrors CliCommandRunnerTests.Run: keeps the AppRunner reference (needed to inspect
    // ShutdownCancellationRegistrations) instead of only its exit code/output.
    private async Task<(int Exit, AppRunner Runner)> Run(string[] args)
    {
        // Serialized with the encryption tests: AIRACCOON_DB_PASSPHRASE is process-global and
        // must be cleared during a run so a dev machine's value cannot poison a fresh-bank test.
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
            var runner = new AppRunner();
            try
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
                var exit = await runner.Run(args);
                return (exit, runner);
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
