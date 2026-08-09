using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Metrics' twin of OtlpTraceExportE2ETests: exercises the OTLP metrics export path through
///     the real MCP transport — a genuine "tools/call" JSON-RPC request over the real HTTP
///     server — against a real OTLP collector stand-in, not the Meter-attached MetricCollector
///     ToolCallMetricsTests uses.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public sealed class OtlpMetricExportE2ETests : IAsyncLifetime
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    private McpServerFactory _factory = null!;
    private CapturingCollector _collector = null!;
    private IDisposable _env = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _env = await AcquireCleanEnvAsync();
        _collector = new CapturingCollector();
        Environment.SetEnvironmentVariable(EndpointVar, _collector.Endpoint);
        Environment.SetEnvironmentVariable(ProtocolVar, "http/protobuf");
        _factory = new McpServerFactory();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _collector.Dispose();
        _env.Dispose();
    }

    [Fact]
    public async Task ToolCallMetric_ReachesTheOtlpCollector_ThroughARealMcpToolCall()
    {
        var client = await _factory.CreateClientAsync();
        try
        {
            await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                null, null, TestContext.Current.CancellationToken);

            _factory.Services.GetRequiredService<MeterProvider>().ForceFlush();
            await _collector.WaitForRequestAsync("/v1/metrics", TimeSpan.FromSeconds(5));

            _collector.RequestedPaths.ShouldContain(path => path.Contains("/v1/metrics", StringComparison.Ordinal));
            _collector.BodyLengthFor("/v1/metrics").ShouldBeGreaterThan(0);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static async Task<IDisposable> AcquireCleanEnvAsync()
    {
        await TestData.EnvVarGate.WaitAsync();
        var originalEndpoint = Environment.GetEnvironmentVariable(EndpointVar);
        var originalProtocol = Environment.GetEnvironmentVariable(ProtocolVar);
        Environment.SetEnvironmentVariable(EndpointVar, null);
        Environment.SetEnvironmentVariable(ProtocolVar, null);
        return new EnvRestore(originalEndpoint, originalProtocol);
    }

    private sealed class EnvRestore(string? originalEndpoint, string? originalProtocol) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EndpointVar, originalEndpoint);
            Environment.SetEnvironmentVariable(ProtocolVar, originalProtocol);
            TestData.EnvVarGate.Release();
        }
    }

    /// <summary>Minimal loopback OTLP/HTTP collector stand-in: records every request path and body length it receives.</summary>
    private sealed class CapturingCollector : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentDictionary<string, long> _bodyLengthByPath = new();

        public CapturingCollector()
        {
            var port = FreePort();
            Endpoint = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Endpoint}/");
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string Endpoint { get; }

        public ConcurrentBag<string> RequestedPaths { get; } = [];

        public long BodyLengthFor(string path) =>
            _bodyLengthByPath.TryGetValue(path, out var length) ? length : 0;

        public async Task WaitForRequestAsync(string path, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (RequestedPaths.Any(p => p.Contains(path, StringComparison.Ordinal)))
                {
                    return;
                }

                await Task.Delay(50);
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                    var path = context.Request.Url!.AbsolutePath;
                    RequestedPaths.Add(path);
                    _bodyLengthByPath[path] = context.Request.ContentLength64;
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
