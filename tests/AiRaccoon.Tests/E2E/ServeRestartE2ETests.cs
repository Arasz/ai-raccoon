using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Observability;
using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     `serve --restart` against a real second process (ADR-0022): the running server exits, the
///     port frees, and what answers afterwards is a different process reporting this binary's
///     version. Also pins the bounded wait — a server that will not go produces a non-zero exit.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public sealed class ServeRestartE2ETests : IAsyncLifetime
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-serve-restart-e2e");
    private Process? _old;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Terminates only the PID this test started, and only if the restart left it alive.</summary>
    public ValueTask DisposeAsync()
    {
        try
        {
            if (_old is { HasExited: false })
            {
                _old.Kill(true);
                _old.WaitForExit(10_000);
            }

            _old?.Dispose();
            TestData.DeleteTempRoot(_dataRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            // Best-effort cleanup.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ARunningServer_IsCycled_AndADifferentProcessAnswersOnThisBuild()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        _old = StartServeProcess(port);
        using var before = await WaitForServerAsync(port);
        before.RootElement.GetProperty("pid").GetInt32().ShouldBe(_old.Id);

        await using var run = StartRestartInProcess(port);
        var url = await WaitForUrlAsync(run);

        // The old process is gone, not merely bypassed.
        await _old.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        _old.ExitCode.ShouldBe(ExitCode.Success);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");

        // What answers now is a different process reporting this binary's version. Same build here,
        // so the version is the assertion that becomes load-bearing after a real `dotnet tool update`.
        using var after = await WaitForServerAsync(port);
        after.RootElement.GetProperty("pid").GetInt32().ShouldNotBe(_old.Id);
        after.RootElement.GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
        after.RootElement.GetProperty("version").GetString().ShouldBe(typeof(ServerInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

        (await run.StopAsync()).ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task AServerThatNeverStops_ExitsNonZeroWithinTheBound_RatherThanHanging()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        // Accepts the shutdown and keeps listening: the port never frees.
        lease.ReleaseForBind();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Accepted,
            TestContext.Current.CancellationToken);
        await using var run = StartRestartInProcess(port);

        var stopwatch = Stopwatch.StartNew();
        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        exit.ShouldBe(ExitCode.RestartTimedOut);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldContain(port.ToString());
        run.Stderr.ShouldNotContain("   at ");
    }

    private Process StartServeProcess(int port)
    {
        var startInfo = new ProcessStartInfo(AiRaccoonProcess.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "--data-root", _dataRoot, "serve", "--port", port.ToString() })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private ServeHarness StartRestartInProcess(int port) =>
        ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"],
            TimeSpan.FromSeconds(180));

    private static async Task<JsonDocument> WaitForServerAsync(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var body = await HttpClient.GetStringAsync($"http://127.0.0.1:{port}/observability",
                    TestContext.Current.CancellationToken);
                var document = JsonDocument.Parse(body);
                if (document.RootElement.GetProperty("name").GetString() == "ai-raccoon")
                {
                    return document;
                }

                document.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Not up yet.
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"no ai-raccoon server answered /observability on port {port}");
    }

    private static Task<string> WaitForUrlAsync(ServeHarness run) =>
        run.WaitForUrlAsync(TestContext.Current.CancellationToken);
}
