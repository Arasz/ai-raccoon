using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AiRaccoon.Observability;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;
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

    [Fact]
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
    // the old unrecorded Activity left behind. AddInMemoryExporter chains onto the same
    // TracerProviderBuilder AddOtlpExport already configured for the real host (OtlpExportTests'
    // bare-ServiceCollection tests use the same trick).
    [Fact]
    public async Task ToolCallSpan_NestsUnderAResolvableRequestSpan()
    {
        var exportedItems = new List<Activity>();
        await using var factory = new McpServerFactory(configureAdditionalServices: services =>
            services.AddOpenTelemetry().WithTracing(t => t.AddInMemoryExporter(exportedItems)));
        var client = await factory.CreateClientAsync();
        try
        {
            await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                null, null, TestContext.Current.CancellationToken);

            factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

            var toolSpan = exportedItems.Single(a => a.OperationName == "tools/call memory_stats");
            var requestSpan = exportedItems.SingleOrDefault(a =>
                a.Source.Name == OtlpNames.AspNetCoreScope && a.SpanId == toolSpan.ParentSpanId);

            requestSpan.ShouldNotBeNull();
            requestSpan.TraceId.ShouldBe(toolSpan.TraceId);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ADR-0021 flags this as unverified: whether SuppressActivityOpenTelemetryData is read lazily
    // per request or cached at type init. This proves the ordering (switch set before
    // WebApplication.CreateBuilder) is early enough either way — the tags actually reach an
    // exported span.
    [Fact]
    public async Task RequestSpan_CarriesHttpSemanticConventionTags()
    {
        var exportedItems = new List<Activity>();
        await using var factory = new McpServerFactory(configureAdditionalServices: services =>
            services.AddOpenTelemetry().WithTracing(t => t.AddInMemoryExporter(exportedItems)));
        var client = await factory.CreateClientAsync();
        try
        {
            await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                null, null, TestContext.Current.CancellationToken);

            factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

            var requestSpan = exportedItems.First(a => a.Source.Name == OtlpNames.AspNetCoreScope);

            requestSpan.GetTagItem("http.request.method").ShouldBe("POST");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    // ADR-0021 "The sampler stays until another lane's test says otherwise": with the hardcoded
    // AlwaysOnSampler in place, this must go red — spans export regardless of OTEL_TRACES_SAMPLER.
    // Removing the override restores it as live configuration; this proves the env var actually
    // reaches the SDK. The assertion is a probe on a source registered only on this factory's
    // provider: Activity.IsAllDataRequested is process-global (the union of every live listener's
    // sample result), so a parallel collection's default-sampled provider marks the shared
    // request/tool/HttpClient spans recorded and they show up here too — asserting on those would
    // test the other lane's sampler, not ours. The probe source has no other listener, so its
    // root span is created (AlwaysOff keeps PropagationData so the trace id survives) but never
    // recorded or exported.
    [Fact]
    public async Task OtelTracesSamplerAlwaysOff_ProducesNoSpans()
    {
        Environment.SetEnvironmentVariable(SamplerVar, "always_off");
        try
        {
            var exportedItems = new List<Activity>();
            await using var factory = new McpServerFactory(configureAdditionalServices: services =>
                services.AddOpenTelemetry().WithTracing(t => t
                    .AddInMemoryExporter(exportedItems)
                    .AddSource(SamplerProbeSource)));
            var client = await factory.CreateClientAsync();
            try
            {
                await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                    null, null, TestContext.Current.CancellationToken);

                using var probe = new ActivitySource(SamplerProbeSource).StartActivity("sampler-probe");
                probe.ShouldNotBeNull();
                probe.Dispose();

                factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

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

    /// <summary>Minimal loopback OTLP/HTTP collector stand-in: records every request path it receives.</summary>
}
