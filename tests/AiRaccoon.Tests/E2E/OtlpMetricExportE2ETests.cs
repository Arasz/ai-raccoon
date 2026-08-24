using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Shouldly;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Metrics' twin of OtlpTraceExportE2ETests: exercises the OTLP metrics export path through
///     the real MCP transport — a genuine "tools/call" JSON-RPC request over the real HTTP
///     server — against a real OTLP collector stand-in, not the Meter-attached MetricCollector
///     ToolCallMetricsTests uses.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public sealed class OtlpMetricExportE2ETests : IAsyncLifetime
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    private McpServerFactory _factory = null!;
    private CapturingCollector _collector = null!;
    private EnvScope _env = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EndpointVar, null), (ProtocolVar, null));
        try
        {
            // Measured on xunit.v3 3.2.2: DisposeAsync does NOT run when InitializeAsync throws,
            // so anything constructed after the gate is taken has to release it itself.
            _collector = new CapturingCollector();
            Environment.SetEnvironmentVariable(EndpointVar, _collector.Endpoint);
            Environment.SetEnvironmentVariable(ProtocolVar, "http/protobuf");
            _factory = new McpServerFactory();
        }
        catch
        {
            await _env.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _factory.DisposeAsync();
            _collector.Dispose();
        }
        finally
        {
            await _env.DisposeAsync();
        }
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
            await _collector.WaitForRequestAsync("/v1/metrics", TestContext.Current.CancellationToken);

            _collector.RequestedPaths.ShouldContain(path => path == "/v1/metrics");
            _collector.BodyLengthFor("/v1/metrics").ShouldBeGreaterThan(0);
        }
        finally
        {
            await client.DisposeAsync();
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
            using var lease = LoopbackPort.Reserve();
            Endpoint = $"http://127.0.0.1:{lease.Port}";
            _listener.Prefixes.Add($"{Endpoint}/");
            lease.ReleaseForBind();
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string Endpoint { get; }

        public ConcurrentBag<string> RequestedPaths { get; } = [];

        public long BodyLengthFor(string path) =>
            _bodyLengthByPath.TryGetValue(path, out var length) ? length : 0;

        /// <summary>Waits for the request itself; the caller's token is the only hang guard (PR #464).</summary>
        public async Task WaitForRequestAsync(string path, CancellationToken cancellationToken)
        {
            while (!RequestedPaths.Any(p => p.Contains(path, StringComparison.Ordinal)))
            {
                await Task.Delay(50, cancellationToken);
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
    }
}
