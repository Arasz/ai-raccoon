using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Observability;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Tools;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     OTel SDK wiring (ADR-0009): opt-in on OTEL_EXPORTER_OTLP_ENDPOINT, web-host only —
///     never stdio (short-lived, per-connection processes) and never the one-shot CLI verbs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ObservabilityCollection.Name)]
public sealed class OtlpExportTests : IDisposable
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string IntervalVar = "OTEL_METRIC_EXPORT_INTERVAL";
    private const string TimeoutVar = "OTEL_METRIC_EXPORT_TIMEOUT";
    private const string ServiceNameVar = "OTEL_SERVICE_NAME";
    private const string ResourceAttributesVar = "OTEL_RESOURCE_ATTRIBUTES";

    // SDK defaults for the periodic metric reader (opentelemetry-dotnet, pinned per ADR-0009) —
    // the ceiling these tests prove we no longer silently fall back to.
    private const int SdkDefaultExportIntervalMilliseconds = 60_000;
    private const int SdkDefaultExportTimeoutMilliseconds = 30_000;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-otlp-export");

    private InfrastructureOptions TestOptions => new() { DataRoot = _dataRoot, Scope = InstallScope.User };

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static readonly OtlpExportState Disabled = new(false, null, null);
    private static readonly OtlpExportState Enabled = new(true, "http://127.0.0.1:4317", "grpc");

    [Fact]
    public void NoEndpoint_RegistersNoTracerProviderOrMeterProvider()
    {
        var services = new ServiceCollection();

        services.AddOtlpExport(TestOptions, Disabled);
        using var provider = services.BuildServiceProvider();

        provider.GetService<TracerProvider>().ShouldBeNull();
        provider.GetService<MeterProvider>().ShouldBeNull();
    }

    // WP4/A4: OTEL_EXPORTER_OTLP_ENDPOINT=127.0.0.1:4317 (no scheme) used to throw UriFormatException
    // inside the exporter-configuration delegate, which runs inside app.StartAsync — the whole MCP
    // server died at boot over an observability opt-in. This must reproduce the real path: the real
    // web host, not the bare-ServiceCollection seam used above.
    [Fact]
    public async Task MalformedEndpoint_MissingScheme_ServerStartsWithExportDisabled()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        await Should.NotThrowAsync(() => host.StartAsync(TestContext.Current.CancellationToken));
        try
        {
            host.Services.GetService(typeof(TracerProvider)).ShouldBeNull();
            host.Services.GetService(typeof(MeterProvider)).ShouldBeNull();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // WP4/A5: `new Uri("localhost:4318")` succeeds with Scheme="localhost" — this is the silent
    // near-miss. An "assert no exception" test would pass today because the bug never throws;
    // asserting the providers were never registered is what makes this check able to fail.
    [Fact]
    public async Task MalformedEndpoint_NonHttpScheme_ServerStartsWithExportDisabled()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "localhost:4318");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            host.Services.GetService(typeof(TracerProvider)).ShouldBeNull();
            host.Services.GetService(typeof(MeterProvider)).ShouldBeNull();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // WP4: an observability opt-in must not degrade the product — the server must still serve a
    // real tool call through the real DI-resolved MemoryTools, not just fail to crash.
    [Fact]
    public async Task MalformedEndpoint_ServerStillServesAToolCall()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var tools = ActivatorUtilities.CreateInstance<MemoryTools>(host.Services);

            var envelope = await tools.Stats("wp4-probe", TestContext.Current.CancellationToken);

            envelope.Data.ShouldNotBeNull();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // T4/J1+J4 join: WP4 warns and disables, WP5 routes quiet's destination; neither half was
    // ever asserted together. This is the one Hermes-spawned backend depends on for a bad
    // OTEL_EXPORTER_OTLP_ENDPOINT to be diagnosable at all under --quiet.
    [Fact]
    public async Task MalformedEndpoint_Quiet_WarningReachesTheLogFile_AndStderrStaysEmpty()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "127.0.0.1:4317");
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User, Quiet = true };
        var config = new ServerConfig(FreePort(), McpTransport.Http, options);

        var originalError = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            using var host = McpServerSetup.CreateServerHost(config);
        }
        finally
        {
            Console.SetError(originalError);
        }

        stderr.ToString().ShouldBeEmpty();
        File.ReadAllText(QuietLogging.LogFilePath(options)).ShouldContain("ai-raccoon: OTLP export disabled");
    }

    [Fact]
    public async Task EndpointSet_RegistersAllThreeMeters_AndTheActivitySource()
    {
        var services = new ServiceCollection();

        services.AddOtlpExport(TestOptions, Enabled);
        await using var provider = services.BuildServiceProvider();

        // Force-builds the providers, then probes each meter/source: Instrument.Enabled /
        // ActivitySource.HasListeners() only turn true once a matching listener is live.
        provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<MeterProvider>();

        using var toolMetrics = new ToolCallMetrics();
        using var queueMetrics = new PromotionQueueMetrics();
        using var runtimeMeter = new Meter("System.Runtime");

        toolMetrics.Meter.CreateCounter<long>("probe_memory_tools").Enabled.ShouldBeTrue();
        queueMetrics.Meter.CreateCounter<long>("probe_promotion_queue").Enabled.ShouldBeTrue();
        runtimeMeter.CreateCounter<long>("probe_system_runtime").Enabled.ShouldBeTrue();
        toolMetrics.ActivitySource.HasListeners().ShouldBeTrue();
    }

    // Guards ActivitySource/sampler/processor wiring outside a real HTTP request context — an
    // ambient unrecorded parent Activity only reproduces inside one (see OtlpTraceExportE2ETests,
    // ADR-0009). AddInMemoryExporter chains onto the same builder; AddOpenTelemetry() is idempotent.
    [Fact]
    public async Task ToolCallSpan_IsForceFlushedToTheConfiguredExporter()
    {
        var services = new ServiceCollection();
        var exportedItems = new List<Activity>();

        services.AddOtlpExport(TestOptions, Enabled);
        services.AddOpenTelemetry().WithTracing(t => t.AddInMemoryExporter(exportedItems));
        await using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();

        using var metrics = new ToolCallMetrics();
        using (var toolActivity = new ToolExecutionActivity(metrics, "probe_tool", "probe-project"))
        {
            toolActivity.RecordInvocation();
        }

        tracerProvider.ForceFlush();

        exportedItems.ShouldNotBeEmpty();
        exportedItems.ShouldContain(a => a.OperationName == "tools/call probe_tool");
    }

    // Metrics' twin of ToolCallSpan_IsForceFlushedToTheConfiguredExporter above: a MetricCollector
    // attached straight to the Meter proves nothing about whether a measurement reaches an
    // exporter (ADR-0009). AddInMemoryExporter chains onto the same metrics builder AddOtlpExport
    // already configured.
    [Fact]
    public async Task ToolCallMetric_IsForceFlushedToTheConfiguredExporter()
    {
        var services = new ServiceCollection();
        var exportedItems = new List<Metric>();

        services.AddOtlpExport(TestOptions, Enabled);
        services.AddOpenTelemetry().WithMetrics(m => m.AddInMemoryExporter(exportedItems));
        await using var provider = services.BuildServiceProvider();
        var meterProvider = provider.GetRequiredService<MeterProvider>();

        using var metrics = new ToolCallMetrics();
        using (var toolActivity = new ToolExecutionActivity(metrics, "probe_tool", "probe-project"))
        {
            toolActivity.RecordInvocation();
        }

        meterProvider.ForceFlush();

        exportedItems.ShouldContain(m => m.Name == OtlpNames.ToolInvocations);
        var invocationMetric = exportedItems.Single(m => m.Name == OtlpNames.ToolInvocations);
        HasToolTag(invocationMetric, "probe_tool").ShouldBeTrue();
        exportedItems.ShouldContain(m => m.Name == OtlpNames.ToolDuration);
    }

    [Fact]
    public async Task EndpointSet_RegistersAListenerForTheAspNetCoreRequestSource()
    {
        // ADR-0021 supersedes ADR-0002/ADR-0009's non-goal: the hosting request span must now be
        // recorded and exported so the tool span's parent resolves. "Microsoft.AspNetCore" is the
        // ActivitySource the framework creates HttpRequestIn on (dotnet/aspnetcore
        // GenericWebHostBuilder.cs), confirmed by OtlpTraceExportE2ETests against a real request —
        // ADR-0021's own text names "Microsoft.AspNetCore.Hosting", which is the hosting *Meter*
        // name (HostingMetrics.cs), a different signal.
        var services = new ServiceCollection();

        services.AddOtlpExport(TestOptions, Enabled);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TracerProvider>();

        new ActivitySource(OtlpNames.AspNetCoreScope).HasListeners().ShouldBeTrue();
    }

    [Fact]
    public async Task StdioHost_NeverWiresTheExporter_EvenWithAnEndpointSet()
    {
        // Stdio hosts recycle too fast to pay the exporter's batch delay / shutdown grace —
        // CreateWebHost only (ADR-0009, "Which host paths get the exporter").
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetService(typeof(TracerProvider)).ShouldBeNull();
        host.Services.GetService(typeof(MeterProvider)).ShouldBeNull();
    }

    // Proves what StdioHost_NeverWiresTheExporter_EvenWithAnEndpointSet doesn't: a stdio tool
    // call's own metric has no listener at all, not just that a provider type is absent
    // (ADR-0009). Asserts a before/after delta rather than an absolute false, the same way
    // CliCommandRunner_NeverWiresTheExporter below does — Enabled/HasListeners() reflect
    // process-wide state, and another test's undisposed provider can leave a listener attached
    // for the rest of the run. GetService(typeof(MeterProvider)) force-builds any provider that
    // exists before the "after" reading — a listener only attaches once built, not merely
    // registered — so this stays a real trap for a future stdio-wiring change.
    [Fact]
    public async Task StdioHost_ToolCallMetric_HasNoListener_SoInvocationsAreNeverExported()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
        using var probeMetrics = new ToolCallMetrics();
        var enabledBefore = probeMetrics.Meter.CreateCounter<long>("probe_stdio_before_call").Enabled;
        var hasListenersBefore = probeMetrics.ActivitySource.HasListeners();

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));
        host.Services.GetService(typeof(MeterProvider));
        var metrics = host.Services.GetRequiredService<ToolCallMetrics>();
        using (var toolActivity = new ToolExecutionActivity(metrics, "probe_tool", "probe-project"))
        {
            toolActivity.RecordInvocation();
        }

        metrics.Meter.CreateCounter<long>("probe_stdio_after_call").Enabled.ShouldBe(enabledBefore);
        metrics.ActivitySource.HasListeners().ShouldBe(hasListenersBefore);
    }

    [Fact]
    public async Task CliCommandRunner_NeverWiresTheExporter()
    {
        // CliCommandRunner hand-composes without a DI container, so this asserts a delta: running
        // a one-shot verb must not add a NEW listener, robust to listeners other concurrent tests
        // already attached (Enabled/HasListeners() reflect process-wide state, not per-test).
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
        using var toolMetrics = new ToolCallMetrics();
        var enabledBefore = toolMetrics.Meter.CreateCounter<long>("probe_cli_never_wires").Enabled;
        var hasListenersBefore = toolMetrics.ActivitySource.HasListeners();
        CliArgs.TryParse(["--data-root", _dataRoot, "access", "default", "show"], out var parsed);
        var config = parsed.Options.ToServerConfig();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliCommandRunner.RunAsync(parsed, config, stdout, stderr, TextReader.Null,
            TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        toolMetrics.Meter.CreateCounter<long>("probe_cli_never_wires_after").Enabled.ShouldBe(enabledBefore);
        toolMetrics.ActivitySource.HasListeners().ShouldBe(hasListenersBefore);
    }

    // ADR-0009 "Default protocol and ports": explicit-endpoint assignment disables the SDK's own
    // signal-path append, so http/protobuf needs it appended here; gRPC stays verbatim.
    [Fact]
    public void HttpProtobuf_AppendsTracesSignalPath()
    {
        var state = new OtlpExportState(true, "http://localhost:4318", "http/protobuf");

        var endpoint = OtlpExport.SignalEndpoint(state, "/v1/traces");

        endpoint.ShouldBe(new Uri("http://localhost:4318/v1/traces"));
    }

    [Fact]
    public void HttpProtobuf_AppendsMetricsSignalPath()
    {
        var state = new OtlpExportState(true, "http://localhost:4318", "http/protobuf");

        var endpoint = OtlpExport.SignalEndpoint(state, "/v1/metrics");

        endpoint.ShouldBe(new Uri("http://localhost:4318/v1/metrics"));
    }

    [Fact]
    public void HttpProtobuf_EndpointAlreadyCarryingSignalPath_IsNotDoubled()
    {
        var state = new OtlpExportState(true, "http://localhost:4318/v1/traces", "http/protobuf");

        var endpoint = OtlpExport.SignalEndpoint(state, "/v1/traces");

        endpoint.ShouldBe(new Uri("http://localhost:4318/v1/traces"));
    }

    [Fact]
    public void HttpProtobuf_TrailingSlashEndpoint_DoesNotProduceDoubleSlash()
    {
        var state = new OtlpExportState(true, "http://localhost:4318/", "http/protobuf");

        var endpoint = OtlpExport.SignalEndpoint(state, "/v1/traces");

        endpoint.ShouldBe(new Uri("http://localhost:4318/v1/traces"));
    }

    // A6: the endpoint already carries the signal path AND a trailing slash — the SDK's own
    // AppendPathIfNotPresent checks the "path + /" form too; the hand-rolled EndsWith guard didn't,
    // so this doubled to ".../v1/traces/v1/traces".
    [Fact]
    public void HttpProtobuf_SignalPathWithTrailingSlash_IsNotDoubled()
    {
        var state = new OtlpExportState(true, "http://host:4318/v1/traces/", "http/protobuf");

        var endpoint = OtlpExport.SignalEndpoint(state, "/v1/traces");

        endpoint.ShouldBe(new Uri("http://host:4318/v1/traces/"));
    }

    // A6: the SDK's own guard uses OrdinalIgnoreCase; the hand-rolled one used Ordinal, so an
    // upper-cased endpoint doubled to ".../V1/TRACES/v1/traces".
    [Fact]
    public void HttpProtobuf_CaseVariedSignalPath_IsNotDoubled()
    {
        var state = new OtlpExportState(true, "HTTP://HOST:4318/V1/TRACES", "http/protobuf");

        var endpoint = OtlpExport.SignalEndpoint(state, "/v1/traces");

        endpoint.ShouldBe(new Uri("HTTP://HOST:4318/V1/TRACES"));
    }

    [Fact]
    public void Grpc_EndpointIsUsedVerbatim()
    {
        var state = new OtlpExportState(true, "http://127.0.0.1:4317", "grpc");

        var endpoint = OtlpExport.SignalEndpoint(state, "/v1/traces");

        endpoint.ShouldBe(new Uri("http://127.0.0.1:4317"));
    }

    [Theory]
    [InlineData("HTTP/PROTOBUF")]
    [InlineData(" http/protobuf ")]
    public void HttpProtobuf_ProtocolCasingAndWhitespace_IsTolerated(string protocol)
    {
        var state = new OtlpExportState(true, "http://localhost:4318", protocol);

        var endpoint = OtlpExport.SignalEndpoint(state, "/v1/traces");

        endpoint.ShouldBe(new Uri("http://localhost:4318/v1/traces"));
    }

    // Pins the per-export timeout to its one named source (ADR-0009). The SDK discards the
    // OtlpExporterOptions instance once it derives the exporter's transmission handler from it
    // (opentelemetry-dotnet, OtlpMetricExporter/OtlpTraceExporter ctors), so TimeoutMilliseconds
    // does not survive into the built provider for reflection — ConfigureExporter is tested
    // directly instead, the same way SignalEndpoint already is.
    [Fact]
    public void ConfigureExporter_SetsTimeoutToTheNamedConstant()
    {
        var options = new OtlpExporterOptions();

        OtlpExport.ConfigureExporter(options, Enabled, "/v1/traces");

        options.TimeoutMilliseconds.ShouldBe(OtlpExportState.ExportTimeoutMilliseconds);
    }

    // Must run through the real CreateWebHost pipeline (ADR-0009 "Configuration channel"): the SDK
    // core reads reader options via DI's IConfiguration, which only the real host clears/restores —
    // the bare-ServiceCollection seam used elsewhere in this file reads real env vars regardless
    // and would mask the bug. Reflection reaches the interval/timeout fields since there is no
    // public surface for them.
    [Fact]
    public async Task HttpHost_MetricExportInterval_IsAppliedToThePeriodicReader_WhenSet()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
        Environment.SetEnvironmentVariable(IntervalVar, "1234");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        PeriodicReaderField(meterProvider, "ExportIntervalMilliseconds").ShouldBe(1234);
    }

    [Fact]
    public async Task HttpHost_MetricExportInterval_LeavesSdkDefault_WhenUnset()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        PeriodicReaderField(meterProvider, "ExportIntervalMilliseconds").ShouldBe(SdkDefaultExportIntervalMilliseconds);
    }

    [Fact]
    public async Task HttpHost_MetricExportTimeout_IsAppliedToThePeriodicReader_WhenSet()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
        Environment.SetEnvironmentVariable(TimeoutVar, "9876");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        PeriodicReaderField(meterProvider, "ExportTimeoutMilliseconds").ShouldBe(9876);
    }

    [Fact]
    public async Task HttpHost_MetricExportTimeout_LeavesSdkDefault_WhenUnset()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        PeriodicReaderField(meterProvider, "ExportTimeoutMilliseconds").ShouldBe(SdkDefaultExportTimeoutMilliseconds);
    }

    // service.name is a fixed product identity: AddService(DefaultServiceName) is registered
    // later in the resource-builder chain than CreateDefault()'s own environment detector, and
    // later-registered-wins, so the explicit call still wins even though OTEL_SERVICE_NAME now
    // reaches that detector too (ADR-0009's own cited proof test for this claim).
    [Fact]
    public async Task HttpHost_ServiceName_StaysAiRaccoon_EvenWhenOtelServiceNameIsSet()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
        Environment.SetEnvironmentVariable(ServiceNameVar, "probe-service");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        ServiceName(host.Services.GetRequiredService<TracerProvider>()).ShouldBe("ai-raccoon");
    }

    [Fact]
    public async Task HttpHost_ServiceName_DefaultsToTheProductName_NotUnknownService()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        var name = ServiceName(host.Services.GetRequiredService<TracerProvider>());
        name.ShouldBe("ai-raccoon");
        name.ShouldNotStartWith("unknown_service");
    }

    [Fact]
    public async Task HttpHost_ServiceName_IsTheSameOnMetricsAndTraces()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        ServiceName(host.Services.GetRequiredService<MeterProvider>())
            .ShouldBe(ServiceName(host.Services.GetRequiredService<TracerProvider>()));
    }

    // OTEL_RESOURCE_ATTRIBUTES reaches the SDK via DI's IConfiguration, the same channel
    // McpServerSetup's config-clearing fix restores (ADR-0009, "Configuration channel"). Reads
    // TracerProvider.Resource via reflection, so it proves the attribute reaches the provider,
    // not that it crosses into an exporter payload on the wire.
    [Fact]
    public async Task HttpHost_ResourceAttributesEnvVar_ReachesTheProvidersResource()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
        Environment.SetEnvironmentVariable(ResourceAttributesVar, "deployment.environment=probe-env");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        ResourceAttribute(host.Services.GetRequiredService<TracerProvider>(), "deployment.environment")
            .ShouldBe("probe-env");
    }

    // The bare-ServiceCollection test elsewhere in this file never starts a host, so it can't
    // show this: without an attached listener after start, ActivitySource.StartActivity returns
    // null and every tool call silently produces no span (ADR-0009's 2026-08-08 update).
    [Fact]
    public async Task StartedHttpHost_AttachesATraceListener_SoToolCallsProduceSpans()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var metrics = host.Services.GetRequiredService<ToolCallMetrics>();
            metrics.ActivitySource.HasListeners().ShouldBeTrue();

            using var activity = metrics.ActivitySource.StartActivity("probe");
            activity.ShouldNotBeNull();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Reads service.name off a provider's Resource.</summary>
    private static string ServiceName(object provider) => ResourceAttribute(provider, "service.name");

    /// <summary>Reads a resource attribute off a provider's Resource; both provider SDKs expose the
    /// Resource internally only, so reflection is the only way to assert what was actually applied.</summary>
    private static string ResourceAttribute(object provider, string key)
    {
        var resource = provider.GetType()
            .GetProperty("Resource", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(provider)!;
        var attributes = (IEnumerable<KeyValuePair<string, object>>)resource.GetType()
            .GetProperty("Attributes")!
            .GetValue(resource)!;
        return (string)attributes.Single(a => a.Key == key).Value;
    }

    /// <summary>ReadOnlyTagCollection has no LINQ surface, only a foreach-only enumerator.</summary>
    private static bool HasToolTag(Metric metric, string toolName)
    {
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "tool" && (string)tag.Value! == toolName)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int PeriodicReaderField(MeterProvider meterProvider, string fieldName)
    {
        var reader = meterProvider.GetType()
            .GetProperty("Reader", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(meterProvider)!;
        return (int)reader.GetType()
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(reader)!;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private ServerConfig Config(McpTransport transport, int port = 7721, TimeSpan idleTimeout = default) =>
        new(port, transport, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User }, idleTimeout);

    private static async Task<IDisposable> AcquireCleanEnvAsync()
    {
        await TestData.EnvVarGate.WaitAsync();
        var originalEndpoint = Environment.GetEnvironmentVariable(EndpointVar);
        var originalProtocol = Environment.GetEnvironmentVariable(ProtocolVar);
        var originalInterval = Environment.GetEnvironmentVariable(IntervalVar);
        var originalTimeout = Environment.GetEnvironmentVariable(TimeoutVar);
        var originalServiceName = Environment.GetEnvironmentVariable(ServiceNameVar);
        var originalResourceAttributes = Environment.GetEnvironmentVariable(ResourceAttributesVar);
        var originalPassphrase = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        Environment.SetEnvironmentVariable(EndpointVar, null);
        Environment.SetEnvironmentVariable(ProtocolVar, null);
        Environment.SetEnvironmentVariable(IntervalVar, null);
        Environment.SetEnvironmentVariable(TimeoutVar, null);
        Environment.SetEnvironmentVariable(ServiceNameVar, null);
        Environment.SetEnvironmentVariable(ResourceAttributesVar, null);
        Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
        return new EnvRestore(originalEndpoint, originalProtocol, originalInterval, originalTimeout,
            originalServiceName, originalResourceAttributes, originalPassphrase);
    }

    private sealed class EnvRestore(
        string? originalEndpoint, string? originalProtocol, string? originalInterval, string? originalTimeout,
        string? originalServiceName, string? originalResourceAttributes, string? originalPassphrase)
        : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EndpointVar, originalEndpoint);
            Environment.SetEnvironmentVariable(ProtocolVar, originalProtocol);
            Environment.SetEnvironmentVariable(IntervalVar, originalInterval);
            Environment.SetEnvironmentVariable(TimeoutVar, originalTimeout);
            Environment.SetEnvironmentVariable(ServiceNameVar, originalServiceName);
            Environment.SetEnvironmentVariable(ResourceAttributesVar, originalResourceAttributes);
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, originalPassphrase);
            TestData.EnvVarGate.Release();
        }
    }
}
