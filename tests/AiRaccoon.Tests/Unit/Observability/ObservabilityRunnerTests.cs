using System.Net;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Observability;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     `serve observability &lt;kind&gt;` (ADR-0008): dials a live server's /observability
///     endpoint, prints the requested monitoring command with its PID filled in, and maps
///     every failure to an actionable stderr line, a non-zero exit, and empty stdout.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ObservabilityCollection.Name)]
public sealed class ObservabilityRunnerTests : IDisposable
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-observability-runner");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task LiveServer_PrintsTheCountersCommand_WithTheServerPid()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var run = await RunObservabilityAsync("counters", port);

            run.Exit.ShouldBe(ExitCode.Success);
            run.Stdout.ShouldBe($"dotnet-counters monitor -p {Environment.ProcessId}{Environment.NewLine}");
            run.Stderr.ShouldBeEmpty();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LiveServer_PrintsTheTraceCommand_WithTheServerPid()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var run = await RunObservabilityAsync("trace", port);

            run.Exit.ShouldBe(ExitCode.Success);
            run.Stdout.ShouldBe($"dotnet-trace collect -p {Environment.ProcessId} --providers {string.Join(',', OtlpNames.Sources)}{Environment.NewLine}");
            run.Stderr.ShouldBeEmpty();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LiveServer_PrintsTheBarePid()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var run = await RunObservabilityAsync("pid", port);

            run.Exit.ShouldBe(ExitCode.Success);
            run.Stdout.ShouldBe($"{Environment.ProcessId}{Environment.NewLine}");
            run.Stderr.ShouldBeEmpty();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task NoServerListening_ReturnsNoServerRunning_WithAStartHint()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;

        lease.ReleaseForBind(); // nothing bound to it
        var run = await RunObservabilityAsync("pid", port);

        run.Exit.ShouldBe(ExitCode.NoServerRunning);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldContain($"no server is listening on port {port}");
        run.Stderr.ShouldContain($"ai-raccoon serve --port {port}");
        run.Stderr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task ForeignListener_ReturnsPortInUse_WithNoStackTrace()
    {
        using var env = await AcquireCleanEnvAsync();
        using var holder = LoopbackPort.Occupy();
        var port = holder.Port;

        var run = await RunObservabilityAsync("pid", port);

        run.Exit.ShouldBe(ExitCode.PortInUse);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldContain($"port {port} is in use by another process");
        run.Stderr.ShouldContain("it is not an ai-raccoon server");
        run.Stderr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task OtlpDisabled_ReturnsOtlpNotEnabled_AndWritesNothingToStdout()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var run = await RunObservabilityAsync("otlp", port);

            run.Exit.ShouldBe(ExitCode.OtlpNotEnabled);
            run.Stdout.ShouldBeEmpty();
            run.Stderr.ShouldContain($"OTLP export is not enabled on the server on port {port}");
            run.Stderr.ShouldContain("OTEL_EXPORTER_OTLP_ENDPOINT");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task OtlpEnabled_PrintsTheEndpointOnStdout_AndTheProtocolOnStderr()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
            Environment.SetEnvironmentVariable(ProtocolVar, "grpc");

            var run = await RunObservabilityAsync("otlp", port);

            run.Exit.ShouldBe(ExitCode.Success);
            run.Stdout.ShouldBe($"http://127.0.0.1:4317{Environment.NewLine}");
            run.Stderr.ShouldContain("ai-raccoon: exporting OTLP over grpc");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AttachedSecondServe_StillReportsTheOwnerPid()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        var first = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await WaitForLineAsync(first, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var secondRoot = TestData.CreateTempRoot("ai-raccoon-observability-attach");
        try
        {
            var second = StartServe(["--data-root", secondRoot, "serve", "--port", port.ToString()]);
            var secondExit = await second.Exit;
            secondExit.ShouldBe(ExitCode.Success);

            var run = await RunObservabilityAsync("pid", port);

            run.Exit.ShouldBe(ExitCode.Success);
            run.Stdout.ShouldBe($"{Environment.ProcessId}{Environment.NewLine}");
        }
        finally
        {
            Directory.Delete(secondRoot, true);
        }

        var firstExit = await StopAsync(first);
        firstExit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task EveryFailurePath_WritesNothingToStdout()
    {
        using var env = await AcquireCleanEnvAsync();

        using var noServerLease = LoopbackPort.Reserve();
        var noServerPort = noServerLease.Port;
        noServerLease.ReleaseForBind();
        var noServerRun = await RunObservabilityAsync("pid", noServerPort);
        noServerRun.Stdout.ShouldBeEmpty();

        using (var holder = LoopbackPort.Occupy())
        {
            var foreignRun = await RunObservabilityAsync("pid", holder.Port);
            foreignRun.Stdout.ShouldBeEmpty();
        }

        using var otlpLease = LoopbackPort.Reserve();
        var otlpDisabledPort = otlpLease.Port;
        var host = McpServerSetup.CreateServerHost(Config(otlpDisabledPort));
        otlpLease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var otlpRun = await RunObservabilityAsync("otlp", otlpDisabledPort);
            otlpRun.Stdout.ShouldBeEmpty();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        using var oldServerLease = LoopbackPort.Reserve();
        var oldServerPort = oldServerLease.Port;
        oldServerLease.ReleaseForBind();
        var oldServer = await StartOldServerWithoutObservabilityAsync(oldServerPort);
        try
        {
            var oldServerRun = await RunObservabilityAsync("pid", oldServerPort);
            oldServerRun.Stdout.ShouldBeEmpty();
            oldServerRun.Exit.ShouldBe(ExitCode.NoServerRunning);
            oldServerRun.Stderr.ShouldContain("does not expose /observability");
        }
        finally
        {
            await oldServer.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private ServerConfig Config(int port) => new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User }, TimeSpan.Zero);

    private static async Task<ObservabilityRun> RunObservabilityAsync(string kind, int port)
    {
        CliArgs.TryParse(["serve", "observability", kind, "--port", port.ToString()], out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await TestData.CreateObservabilityRunner().RunAsync(parsed, new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);
        return new ObservabilityRun(exit, stdout.ToString(), stderr.ToString());
    }

    private static ServeRun StartServe(string[] args)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        var config = parsed.Options.ToServerConfig();
        var stdout = new LockingWriter();
        var stderr = new LockingWriter();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var exit = TestData.CreateNodeRunner(parsed.ServerConfig.Options).RunAsync(parsed, new StandardStreams(TextReader.Null, stdout, stderr), cts.Token);
        return new ServeRun(exit, stdout, stderr, cts);
    }

    private static async Task<int> StopAsync(ServeRun run)
    {
        await run.Cts.CancelAsync();
        return await run.Exit;
    }

    private static async Task<string> WaitForLineAsync(ServeRun run, Func<string, bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = run.Stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (line is not null && predicate(line.TrimEnd('\r')))
            {
                return line.TrimEnd('\r');
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"timed out waiting for serve output; stderr: {run.Stderr}");
    }

    /// <summary>A stand-in for an ai-raccoon build without the observability endpoint: a real HTTP
    /// listener with no /observability route mapped, so GET /observability naturally 404s.</summary>
    private static async Task<WebApplication> StartOldServerWithoutObservabilityAsync(int port)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.MapGet("/mcp", () => Results.Ok());
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }


    private static async Task<IDisposable> AcquireCleanEnvAsync()
    {
        await TestData.EnvVarGate.WaitAsync();
        var originalEndpoint = Environment.GetEnvironmentVariable(EndpointVar);
        var originalProtocol = Environment.GetEnvironmentVariable(ProtocolVar);
        var originalPassphrase = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        Environment.SetEnvironmentVariable(EndpointVar, null);
        Environment.SetEnvironmentVariable(ProtocolVar, null);
        Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
        return new EnvRestore(originalEndpoint, originalProtocol, originalPassphrase);
    }

    // ── Helpers ──

    private sealed record ObservabilityRun(int Exit, string Stdout, string Stderr);

    private sealed record ServeRun(Task<int> Exit, LockingWriter Stdout, LockingWriter Stderr, CancellationTokenSource Cts);

    private sealed class EnvRestore(string? originalEndpoint, string? originalProtocol, string? originalPassphrase) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EndpointVar, originalEndpoint);
            Environment.SetEnvironmentVariable(ProtocolVar, originalProtocol);
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, originalPassphrase);
            TestData.EnvVarGate.Release();
        }
    }

    /// <summary>Thread-safe capture for the runner's stdout/stderr writers.</summary>
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
