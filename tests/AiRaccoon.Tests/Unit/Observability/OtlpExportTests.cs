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
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     OTel SDK wiring (ADR 0009): opt-in on OTEL_EXPORTER_OTLP_ENDPOINT, web-host only —
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

    // SDK defaults (PeriodicExportingMetricReaderHelper), measured against opentelemetry-dotnet
    // core-1.17.0 source — the ceiling these tests prove we no longer silently fall back to.
    private const int SdkDefaultExportIntervalMilliseconds = 60_000;
    private const int SdkDefaultExportTimeoutMilliseconds = 30_000;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-otlp-export");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static readonly OtlpExportState Disabled = new(false, null, null);
    private static readonly OtlpExportState Enabled = new(true, "http://127.0.0.1:4317", "grpc");

    [Fact]
    public void NoEndpoint_RegistersNoTracerProviderOrMeterProvider()
    {
        var services = new ServiceCollection();

        services.AddOtlpExport(Disabled);
        using var provider = services.BuildServiceProvider();

        provider.GetService<TracerProvider>().ShouldBeNull();
        provider.GetService<MeterProvider>().ShouldBeNull();
    }

    [Fact]
    public async Task EndpointSet_RegistersAllThreeMeters_AndTheActivitySource()
    {
        var services = new ServiceCollection();

        services.AddOtlpExport(Enabled);
        await using var provider = services.BuildServiceProvider();

        // Force-build the providers, then prove each configured meter/source is actually
        // listened to: Instrument.Enabled / ActivitySource.HasListeners() only turn true
        // once a MeterListener/ActivityListener matching that name is live.
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

    [Fact]
    public async Task EndpointSet_DoesNotRegisterAspNetCoreInstrumentation()
    {
        // Pins ADR 0002/0009's standing non-goal: no Kestrel span per request. ASP.NET
        // Core's own HTTP-request ActivitySource is "Microsoft.AspNetCore" (confirmed in
        // dotnet/aspnetcore's WebHostBuilder.cs); it must never gain a listener here.
        var services = new ServiceCollection();

        services.AddOtlpExport(Enabled);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TracerProvider>();

        new ActivitySource("Microsoft.AspNetCore").HasListeners().ShouldBeFalse();
    }

    [Fact]
    public async Task StdioHost_NeverWiresTheExporter_EvenWithAnEndpointSet()
    {
        // Owner reversal: stdio hosts recycle roughly every 5 minutes, too short-lived to
        // pay the exporter's batch delay / provider shutdown grace — CreateWebHost only.
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetService(typeof(TracerProvider)).ShouldBeNull();
        host.Services.GetService(typeof(MeterProvider)).ShouldBeNull();
    }

    [Fact]
    public async Task CliCommandRunner_NeverWiresTheExporter()
    {
        // CliCommandRunner hand-composes without a DI container: there is no IServiceCollection
        // to wire OTel into. Proven behaviorally — even with the endpoint set, running a
        // one-shot verb must not attach a NEW listener to the application instruments.
        // Delta, not an absolute false: Enabled/HasListeners() reflect *process-wide* listener
        // state, so this only asserts CliCommandRunner itself changes nothing, robust to any
        // unrelated listener another concurrently running test may have already attached.
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

    // ADR 0009 "Default protocol and ports": explicit-endpoint assignment disables the SDK's
    // own AppendSignalPathToEndpoint behavior, so http/protobuf needs the per-signal path
    // appended here or the collector never receives traces/metrics — silently, per the ADR's
    // failure posture. gRPC must stay verbatim: one endpoint, signal encoded in the RPC method.
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

    // ADR 0009 "Configuration channel": OTEL_METRIC_EXPORT_INTERVAL/_TIMEOUT are read explicitly
    // for the same cleared-sources reason as the endpoint. Unlike SignalEndpoint, this cannot be
    // proven with a pure-function unit test: the SDK core (not the exporter package) registers
    // PeriodicExportingMetricReaderOptions through RegisterOptionsFactory bound to DI's
    // IConfiguration, which McpServerSetup clears — so only the real CreateWebHost pipeline, with
    // the real (cleared) host IConfiguration in play, can show whether the value actually reaches
    // the reader. The bare ServiceCollection seam used elsewhere in this file does NOT reproduce
    // this: with no host builder, AddOpenTelemetrySharedProviderBuilderServices's
    // TryAddSingleton<IConfiguration> takes effect and reads real env vars regardless of our fix,
    // masking the bug either way. Reflection reaches the internal Reader property (MeterProviderSdk)
    // and the internal ExportIntervalMilliseconds/ExportTimeoutMilliseconds fields
    // (PeriodicExportingMetricReader) — there is no public surface to assert the applied values.
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

    // service.name is a deliberate fixed product identity (PR #107), not an operator knob: now that
    // McpServerSetup re-admits OTEL_* variables into config (this fix), the SDK's own environment
    // AddService wins the resource merge over CreateDefault()'s own environment detector, so
    // OTEL_SERVICE_NAME only takes effect because Resolve() reads it into state.ServiceName.
    // Operators running several environments need this knob to tell them apart.
    [Fact]
    public async Task HttpHost_ServiceName_HonoursOtelServiceNameWhenSet()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
        Environment.SetEnvironmentVariable(ServiceNameVar, "probe-service");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        ServiceName(host.Services.GetRequiredService<TracerProvider>()).ShouldBe("probe-service");
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
        Environment.SetEnvironmentVariable(ServiceNameVar, "shared-name");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        ServiceName(host.Services.GetRequiredService<MeterProvider>())
            .ShouldBe(ServiceName(host.Services.GetRequiredService<TracerProvider>()));
    }

    // Root-cause test for the config-clearing bug (fixed here): OTEL_RESOURCE_ATTRIBUTES is parsed
    // by the SDK's own environment detector through DI's IConfiguration, same mechanism that
    // swallowed the export interval and the OTLP headers/compression. Before the fix, McpServerSetup
    // clearing all config sources meant this attribute never reached the exported Resource.
    [Fact]
    public async Task HttpHost_ResourceAttributesEnvVar_ReachesTheExportedResource()
    {
        using var env = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://127.0.0.1:4317");
        Environment.SetEnvironmentVariable(ResourceAttributesVar, "deployment.environment=probe-env");

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        ResourceAttribute(host.Services.GetRequiredService<TracerProvider>(), "deployment.environment")
            .ShouldBe("probe-env");
    }

    // The bare-ServiceCollection listener test elsewhere in this file cannot show this: it never
    // starts a host, so it never exercises the path a running serve takes. If no listener is
    // attached after the host starts, ActivitySource.StartActivity returns null and every tool
    // call produces no span at all — traces would silently never leave the process.
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
