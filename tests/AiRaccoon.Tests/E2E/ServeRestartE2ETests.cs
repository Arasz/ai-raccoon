using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Observability;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Serve;
using AiRaccoon.Tests.Unit.Setup.Serve;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     `serve --restart` against a real second process (ADR-0022): the running server exits, the
///     port frees, and what answers afterwards is a different process reporting this binary's
///     version. Also pins the bounded wait — a server that will not go produces a non-zero exit.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
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
            Directory.Delete(_dataRoot, true);
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
        using var env = await AcquireCleanEnvAsync();
        var port = AiRaccoonProcess.FreePort();
        _old = StartServeProcess(port);
        using var before = await WaitForServerAsync(port);
        before.RootElement.GetProperty("pid").GetInt32().ShouldBe(_old.Id);

        var run = StartRestartInProcess(port);
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

        await run.Cts.CancelAsync();
        (await run.Exit).ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task AServerThatNeverStops_ExitsNonZeroWithinTheBound_RatherThanHanging()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = AiRaccoonProcess.FreePort();
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        // Accepts the shutdown and keeps listening: the port never frees.
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Accepted,
            TestContext.Current.CancellationToken);
        var run = StartRestartInProcess(port);

        var stopwatch = Stopwatch.StartNew();
        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        exit.ShouldBe(ExitCode.RestartFailed);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
        run.Stdout.ToString().ShouldBeEmpty();
        run.Stderr.ToString().ShouldContain(port.ToString());
        run.Stderr.ToString().ShouldNotContain("   at ");
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

    private ServeRun StartRestartInProcess(int port)
    {
        CliArgs.TryParse(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"], out var parsed);
        parsed.Errors.ShouldBeEmpty();
        var stdout = new LockingWriter();
        var stderr = new LockingWriter();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        return new ServeRun(ServeRunner.RunAsync(parsed, parsed.Options.ToServerConfig(), stdout, stderr, cts.Token),
            stdout, stderr, cts);
    }

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

    private static async Task<string> WaitForUrlAsync(ServeRun run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var line = run.Stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (line is not null && line.StartsWith("http://", StringComparison.Ordinal))
            {
                return line.TrimEnd('\r');
            }

            if (run.Exit.IsCompleted)
            {
                throw new InvalidOperationException($"serve --restart exited {await run.Exit}; stderr: {run.Stderr}");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"timed out waiting for serve --restart; stderr: {run.Stderr}");
    }

    private static async Task<IDisposable> AcquireCleanEnvAsync()
    {
        await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
        var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
        return new EnvRestore(original);
    }

    private sealed record ServeRun(Task<int> Exit, LockingWriter Stdout, LockingWriter Stderr, CancellationTokenSource Cts);

    private sealed class EnvRestore(string? original) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            TestData.EnvVarGate.Release();
        }
    }

    private sealed class LockingWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly Lock _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_lock)
            {
                _buffer.AppendLine(value);
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return _buffer.ToString();
            }
        }
    }
}
