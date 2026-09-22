using System.Globalization;
using System.Net.Http.Json;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Observability;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     Owner ruling 2026-09-22: a settings verb never owns its own backend — it attaches to the
///     shared server on the configured port and starts one there only when nothing answers, exactly
///     as every server-routed CLI verb did before F70/K1's private spawn briefly reached this path.
///     Driven through the real <c>ai-raccoon</c> binary as separate OS processes (not a fake
///     launcher), because the defect this measures — a settings command leaving its own backend
///     behind — only shows up at the process level: before this ruling, running three plain
///     invocations against a scratch root left three live `serve` processes, none of them on the
///     port a later command could find (each was its own private spawn on an ephemeral port).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CliSettingsSharedBackendTests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);
    private static readonly HttpClient ObservabilityClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string _dataRoot = TestData.CreateTempRoot("cli-settings-shared-lifetime");
    private LoopbackPort? _portLease;
    private int _port;

    public ValueTask InitializeAsync()
    {
        _portLease = LoopbackPort.Reserve();
        _port = _portLease.Port;
        _portLease.ReleaseForBind();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, _port, CancellationToken.None);
        _portLease?.Dispose();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     The gate: three separate `settings sweep show` invocations against a scratch root with no
    ///     server running leave exactly one shared backend on the configured port, reused by commands
    ///     2 and 3 — not one apiece. Red on the pre-ruling private-spawn default: nothing ever answers
    ///     the configured port at all, because each command started its own backend on an ephemeral
    ///     port instead.
    /// </summary>
    [RetryFact]
    public async Task NSettingsCommands_AgainstAScratchRootWithNoServerRunning_ReuseOneSharedBackendOnTheConfiguredPort()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        int? firstPid = null;
        for (var i = 1; i <= 3; i++)
        {
            var run = await RaccoonProcess.RunAsync(
                ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), "settings", "sweep", "show"],
                HardCap, TestContext.Current.CancellationToken);
            run.ExitCode.ShouldBe(0, $"command {i} failed; stderr: {run.Stderr}");

            var info = await FetchServerInfoAsync(_port, TestContext.Current.CancellationToken);
            info.ShouldNotBeNull(
                $"command {i} left no shared backend answering on the configured port {_port} — it must have " +
                "started its own instead of attaching");
            firstPid ??= info!.Pid;
            info!.Pid.ShouldBe(firstPid.Value,
                $"command {i} was served by pid {info.Pid}, not the backend (pid {firstPid}) the first command " +
                "left running on the configured port — each command started its own backend instead of reusing one");
        }
    }

    /// <summary>
    ///     F38 residual (owner ruling N1): a settings command's only prior word about the backend was
    ///     that it was starting — nothing said it would outlive the command or how to stop it. Red
    ///     before the disclosure line exists.
    /// </summary>
    [RetryFact]
    public async Task ASettingsCommand_PrintsThatTheBackendOutlivesIt_AndHowToStopIt()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), "settings", "sweep", "show"],
            HardCap, TestContext.Current.CancellationToken);

        run.ExitCode.ShouldBe(0, $"command failed; stderr: {run.Stderr}");
        run.Stderr.Contains("keeps running after this command exits").ShouldBe(true,
            $"stderr never disclosed that the backend outlives the command; full stderr:\n{run.Stderr}");
        run.Stderr.Contains($"--port {_port}").ShouldBe(true,
            $"the disclosure line must name the port to stop; full stderr:\n{run.Stderr}");
    }

    private static async Task<ServerInfo?> FetchServerInfoAsync(int port, CancellationToken ctx)
    {
        try
        {
            using var response = await ObservabilityClient.GetAsync($"http://127.0.0.1:{port}/observability", ctx);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ServerInfo>(cancellationToken: ctx)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
