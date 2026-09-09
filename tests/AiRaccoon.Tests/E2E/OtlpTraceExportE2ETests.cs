using System.Diagnostics;
using AiRaccoon.Observability;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;
using xRetry.v3;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Exercises the OTLP export path through the real MCP transport: a genuine "tools/call"
///     JSON-RPC request over the real HTTP server, exactly the path a live client takes, against
///     a real OTLP collector stand-in — not a hand-built Activity or an in-memory exporter.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public sealed class OtlpTraceExportE2ETests : IAsyncLifetime
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string SamplerVar = "OTEL_TRACES_SAMPLER";
    private const string SamplerProbeSource = "AiRaccoon.Tests.SamplerProbe";

    /// <summary>How long a late span gets to reach the listener before the test calls it missing.</summary>
    private static readonly TimeSpan SpanSettleTimeout = TimeSpan.FromSeconds(30);
    private CapturingCollector _collector = null!;
    private EnvScope _env = null!;

    private McpServerFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EndpointVar, null), (ProtocolVar, null), (SamplerVar, null));
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

    [RetryFact]
    public async Task ToolCallSpan_ReachesTheOtlpCollector_ThroughARealMcpToolCall()
    {
        var client = await _factory.CreateClientAsync();
        try
        {
            await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                null, null, TestContext.Current.CancellationToken);

            _factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
            await _collector.WaitForRequestAsync("/v1/traces", TestContext.Current.CancellationToken);

            _collector.RequestedPaths.ShouldContain(path => path == "/v1/traces");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ADR-0021: registering the ASP.NET Core request source (OtlpNames.AspNetCoreScope) fixes the
    // orphan — the tool span's parent must now be a recorded, exported span, not the dangling id
    // the old unrecorded Activity left behind. P2/ADR-0020 removed entry-point hosting, so the
    // chaining seam (AddInMemoryExporter onto the app's own builder) is gone with it: an
    // ActivityListener observes the same spans off the same sources instead.
    [RetryFact]
    public async Task ToolCallSpan_NestsUnderAResolvableRequestSpan()
    {
        using var listener = SpanCapture.ListenTo(OtlpNames.MemoryToolsScope, OtlpNames.AspNetCoreScope);
        await using var factory = new McpServerFactory();
        var client = await factory.CreateClientAsync();
        try
        {
            await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                null, null, TestContext.Current.CancellationToken);

            // The listener appends under a lock on another thread: snapshot under the same lock
            // and poll — a bare Single races the stop callback and flakes under load.
            Activity? toolSpan = null;
            Activity? requestSpan = null;
            var settled = await WaitByPolling.WaitForAsync(async () =>
            {
                var ended = listener.Snapshot();
                toolSpan = ended.SingleOrDefault(a => a.OperationName == "tools/call memory_stats");
                requestSpan = toolSpan is null
                    ? null
                    : ended.SingleOrDefault(a => a.Source.Name == OtlpNames.AspNetCoreScope && a.SpanId == toolSpan.ParentSpanId);
                return toolSpan is not null && requestSpan is not null;
            }, WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, SpanSettleTimeout, TimeProvider.System,
                TestContext.Current.CancellationToken);

            settled.ShouldBeTrue("the tool span and its request parent reach the listener");
            requestSpan!.TraceId.ShouldBe(toolSpan!.TraceId);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ADR-0021 flags this as unverified: whether SuppressActivityOpenTelemetryData is read lazily
    // per request or cached at type init. This proves the ordering (switch set before
    // WebApplication.CreateBuilder) is early enough either way — the tags actually reach an
    // observed span. Same P2 seam removal as above: ActivityListener instead of InMemoryExporter.
    [RetryFact]
    public async Task RequestSpan_CarriesHttpSemanticConventionTags()
    {
        using var listener = SpanCapture.ListenTo(OtlpNames.MemoryToolsScope, OtlpNames.AspNetCoreScope);
        await using var factory = new McpServerFactory();
        var client = await factory.CreateClientAsync();
        try
        {
            await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                null, null, TestContext.Current.CancellationToken);

            Activity? settledRequestSpan = null;
            var requestSettled = await WaitByPolling.WaitForAsync(async () =>
            {
                settledRequestSpan = listener.Snapshot().FirstOrDefault(a => a.Source.Name == OtlpNames.AspNetCoreScope);
                return settledRequestSpan is not null;
            }, WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, SpanSettleTimeout, TimeProvider.System,
                TestContext.Current.CancellationToken);

            requestSettled.ShouldBeTrue("the request span reaches the listener");
            settledRequestSpan!.GetTagItem("http.request.method").ShouldBe("POST");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ADR-0021 "The sampler stays until another lane's test says otherwise": OTEL_TRACES_SAMPLER
    // is live configuration (the hardcoded AlwaysOn override the original comment names is gone
    // from OtlpExport.cs — "OTEL_TRACES_SAMPLER is live configuration again"). Same P2 seam
    // removal: the probe runs against a test-owned provider built from the same SDK env plumbing
    // instead of the app's builder. The probe source is registered only there, so asserting on it
    // cannot observe a parallel collection's default-sampled provider (IsAllDataRequested is the
    // union of every live listener's sample result) — same isolation argument as before.
    [RetryFact]
    public async Task OtelTracesSamplerAlwaysOff_ProducesNoSpans()
    {
        Environment.SetEnvironmentVariable(SamplerVar, "always_off");
        try
        {
            var exportedItems = new List<Activity>();
            using var testProvider = Sdk.CreateTracerProviderBuilder()
                .AddSource(SamplerProbeSource)
                .AddInMemoryExporter(exportedItems)
                .Build();
            await using var factory = new McpServerFactory();
            var client = await factory.CreateClientAsync();
            try
            {
                await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                    null, null, TestContext.Current.CancellationToken);

                using var probe = new ActivitySource(SamplerProbeSource).StartActivity("sampler-probe");
                probe.ShouldNotBeNull();
                // Secondary observable: the sampling decision itself, not just the exporter's silence.
                probe.IsAllDataRequested.ShouldBeFalse();
                probe.Dispose();

                testProvider.ForceFlush();

                exportedItems.ShouldNotContain(a => a.Source.Name == SamplerProbeSource);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }
        finally
        {
            // Back to the class scope's baseline, not to the machine's: _env snapshotted the real
            // original and puts it back at teardown. Restoring it here used to lose it.
            Environment.SetEnvironmentVariable(SamplerVar, null);
        }
    }

    /// <summary>
    ///     Process-wide span tap for the two sources a tool call exercises, narrowed to exactly
    ///     those scopes so parallel suites never observe this test's sampling decisions.
    /// </summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;

        private SpanCapture(ActivityListener listener) => _listener = listener;

        public List<Activity> Ended { get; } = [];

        /// <summary>Locked copy of the stopped spans: the stop callback appends on another thread.</summary>
        public List<Activity> Snapshot()
        {
            lock (Ended)
            {
                return [.. Ended];
            }
        }

        public static SpanCapture ListenTo(params string[] sources)
        {
            var capture = new SpanCapture(new ActivityListener());
            capture._listener.ShouldListenTo = source => sources.Contains(source.Name, StringComparer.Ordinal);
            capture._listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData;
            capture._listener.ActivityStopped = activity =>
            {
                lock (capture.Ended)
                {
                    capture.Ended.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(capture._listener);
            return capture;
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Minimal loopback OTLP/HTTP collector stand-in: records every request path it receives.</summary>
}
