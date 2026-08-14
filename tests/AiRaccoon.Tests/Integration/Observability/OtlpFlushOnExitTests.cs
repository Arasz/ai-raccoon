using System.Collections.Concurrent;
using System.Net;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Observability;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Extensions;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using AiRaccoon.Tests.Unit.Observability;
using Xunit;

namespace AiRaccoon.Tests.Integration.Observability;

/// <summary>
///     A3 (docs/reviews/2026-08-09-otlp-export-review.md): provider disposal is the OTel SDK's
///     only flush trigger, and <see cref="AiRaccoon.Setup.Extensions.HostExtensions.RunAsync"/> — the bare-launch path
///     Program.cs:53 uses — must dispose the host on shutdown the way ServeRunner.cs:90-93
///     already does.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ObservabilityCollection.Name)]
public sealed class OtlpFlushOnExitTests : IDisposable
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    // Short on purpose: the SDK's own batch processor also auto-flushes on a periodic ~5s
    // schedule regardless of disposal, so a wait budget anywhere near that would pass even
    // without the fix (the process never truly exits inside this test). Disposal's
    // Shutdown(OtlpExportState.ExportTimeoutMilliseconds) runs synchronously inside DisposeAsync
    // and blocks until the export attempt finishes, so a correctly-disposing host has already
    // delivered the export by the time RunAsync returns — this only covers HTTP round-trip slop.
    private static readonly TimeSpan CollectorWaitBudget = TimeSpan.FromSeconds(3);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-otlp-flush-on-exit");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task ExitingTheBareHostPath_FlushesTheSpanToTheCollector_WithoutAnExplicitForceFlush()
    {
        // The collector owns the endpoint, so it is built before the scope that publishes it;
        // it touches no environment variable, so it needs no gate of its own.
        using var collector = new CapturingCollector();
        await using var env = await AcquireCleanEnvAsync(collector.Endpoint, "http/protobuf");

        using var lease = LoopbackPort.Reserve();
        var config = Config(lease.Port);
        var host = McpServerSetup.CreateServerHost(config);
        var shutdown = new CancellationTokenSource();

        // The exact production shape (Program.cs:53): IHost.RunAsync starts the host, waits for
        // the token, and returns — whatever it does or doesn't dispose on the way out.
        lease.ReleaseForBind();
        var runTask = host.RunAsync(config, shutdown.Token);

        var metrics = await AttachedToolCallMetricsAsync(host);
        using (var activity = new ToolExecutionActivity(metrics, "probe_tool", "probe-project"))
        {
            activity.RecordInvocation();
        }

        shutdown.Cancel();
        await runTask;

        await collector.WaitForRequestAsync("/v1/traces", CollectorWaitBudget);

        collector.RequestedPaths.ShouldContain(path => path.Contains("/v1/traces", StringComparison.Ordinal));
    }

    /// <summary>Polls until the exporter's listener attaches — StartActivity returns null with
    /// none attached, so recording before this point would silently produce no span.</summary>
    private static async Task<ToolCallMetrics> AttachedToolCallMetricsAsync(IHost host)
    {
        var metrics = host.Services.GetRequiredService<ToolCallMetrics>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!metrics.ActivitySource.HasListeners() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return metrics;
    }

    private ServerConfig Config(int port) => new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });


    // Serialized with the other env-var tests via EnvScope, which takes TestData.EnvVarGate
    // (the OTEL_* vars are process-global).
    private static ValueTask<EnvScope> AcquireCleanEnvAsync(string? endpoint = null, string? protocol = null) =>
        EnvScope.AcquireAsync(TestContext.Current.CancellationToken, (EndpointVar, endpoint), (ProtocolVar, protocol));

    /// <summary>Minimal loopback OTLP/HTTP collector stand-in: records every request path it receives.</summary>
    private sealed class CapturingCollector : IDisposable
    {
        private readonly Task _acceptLoop;
        private readonly CancellationTokenSource _cts = new();
        private readonly HttpListener _listener = new();

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

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }

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
                    RequestedPaths.Add(context.Request.Url!.AbsolutePath);
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
