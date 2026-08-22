using System.Net;
using System.Reflection;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Observability;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Observability;

/// <summary>
///     GET /observability (ADR-0008): server identity + PID + OTLP state, mapped only in
///     HTTP/serve mode, and never counted as watchdog activity.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ObservabilityEndpointTests : IDisposable
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private static readonly HttpClient HttpClient = new();
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-observability-endpoint");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task Get_ReturnsThisProcessPidAndTheServerName()
    {
        await using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, port));

        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var json = await GetObservabilityAsync(port);

            json.RootElement.GetProperty("name").GetString().ShouldBe("ai-raccoon");
            json.RootElement.GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Get_ReportsTheRunningBinarysVersion()
    {
        // The discriminator `serve --restart` needs (ADR-0022): without it nothing on the wire
        // says which binary answers, which is exactly the mixed-binary lockout ADR-0019 names.
        await using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, port));

        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var json = await GetObservabilityAsync(port);

            json.RootElement.GetProperty("version").GetString()
                .ShouldBe(typeof(ServerInfo).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Get_ReportsOtlpDisabled_WhenNoEndpointIsSet()
    {
        await using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, port));

        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var json = await GetObservabilityAsync(port);

            var otlp = json.RootElement.GetProperty("otlp");
            otlp.GetProperty("enabled").GetBoolean().ShouldBeFalse();
            otlp.GetProperty("endpoint").ValueKind.ShouldBe(JsonValueKind.Null);
            otlp.GetProperty("protocol").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Get_ReportsOtlpEndpointAndProtocol_WhenSet()
    {
        await using var env = await AcquireCleanEnvAsync("http://127.0.0.1:4317", "grpc");
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, port));

        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var json = await GetObservabilityAsync(port);

            var otlp = json.RootElement.GetProperty("otlp");
            otlp.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            otlp.GetProperty("endpoint").GetString().ShouldBe("http://127.0.0.1:4317");
            otlp.GetProperty("protocol").GetString().ShouldBe("grpc");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Get_IsNotMapped_InStdioMode()
    {
        // Stdio-only runs on a plain app host with no web server at all (ADR-0008): there
        // is no port to map /observability onto, so the absence of IServer proves it.
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetService(typeof(IServer)).ShouldBeNull();
    }

    [Fact]
    public async Task Get_DoesNotResetTheIdleWatchdog()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var time = new FakeTimeProvider(FixedNow);
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Http, port, TimeSpan.FromSeconds(2)), [McpTransport.Http], time);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(100, TestContext.Current.CancellationToken); // watchdog timer registers
            time.Advance(TimeSpan.FromSeconds(1)); // t0+1s
            await Task.Delay(100, TestContext.Current.CancellationToken);

            // Poll /observability repeatedly right up to the original deadline; if this
            // endpoint reset the watchdog like /mcp does, the deadline would move.
            await GetObservabilityAsync(port);
            time.Advance(TimeSpan.FromSeconds(0.9)); // t0+1.9s: still before the deadline
            await Task.Delay(100, TestContext.Current.CancellationToken);
            await GetObservabilityAsync(port);

            time.Advance(TimeSpan.FromSeconds(0.1)); // t0+2s: exactly the deadline — not past
            await Task.Delay(100, TestContext.Current.CancellationToken);
            lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse();

            time.Advance(TimeSpan.FromSeconds(0.5)); // t0+2.5s: the unchanged deadline's tick fires
            await Task.Delay(100, TestContext.Current.CancellationToken);
            lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeTrue();

            await host.WaitForShutdownAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<JsonDocument> GetObservabilityAsync(int port)
    {
        using var response = await HttpClient.GetAsync($"http://127.0.0.1:{port}/observability",
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body);
    }

    private ServerConfig Config(McpTransport transport, int port = 0, TimeSpan idleTimeout = default) =>
        new(port, transport, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User }, idleTimeout);


    // Serialized with the other env-var tests via EnvScope, which takes TestData.EnvVarGate
    // (the OTEL_* vars are process-global).
    private static ValueTask<EnvScope> AcquireCleanEnvAsync(string? endpoint = null, string? protocol = null) =>
        EnvScope.AcquireAsync(TestContext.Current.CancellationToken, (EndpointVar, endpoint), (ProtocolVar, protocol));
}
