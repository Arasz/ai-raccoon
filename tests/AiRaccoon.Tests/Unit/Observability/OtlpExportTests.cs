using System.Diagnostics;
using System.Diagnostics.Metrics;
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

    private ServerConfig Config(McpTransport transport, int port = 7721, TimeSpan idleTimeout = default) =>
        new(port, transport, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User }, idleTimeout);

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
}
