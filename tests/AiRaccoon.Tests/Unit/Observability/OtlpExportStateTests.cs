using AiRaccoon.Observability;
using AiRaccoon.Tests;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>Resolves OTLP export state from OTEL_* environment variables; see ADR 0009.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class OtlpExportStateTests
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    [Fact]
    public async Task NoEndpointSet_IsDisabled()
    {
        using var _ = await AcquireCleanEnvAsync();

        var state = OtlpExportState.Resolve();

        state.ShouldBe(new OtlpExportState(false, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceEndpoint_IsDisabled(string endpoint)
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, endpoint);

        var state = OtlpExportState.Resolve();

        state.ShouldBe(new OtlpExportState(false, null, null));
    }

    [Fact]
    public async Task EndpointSet_IsEnabled_AndDefaultsToGrpc()
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");

        var state = OtlpExportState.Resolve();

        state.ShouldBe(new OtlpExportState(true, "http://localhost:4317", "grpc"));
    }

    [Fact]
    public async Task ProtocolSet_IsReported()
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4318");
        Environment.SetEnvironmentVariable(ProtocolVar, "http/protobuf");

        var state = OtlpExportState.Resolve();

        state.ShouldBe(new OtlpExportState(true, "http://localhost:4318", "http/protobuf"));
    }

    // ADR 0009 "Configuration channel": OTEL_METRIC_EXPORT_INTERVAL is read explicitly for the
    // same cleared-sources reason as the endpoint — the SDK's own PeriodicExportingMetricReaderOptions
    // resolves through DI's IConfiguration, which McpServerSetup clears.
    [Fact]
    public async Task NoMetricExportInterval_ResolvesToNull()
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");

        var state = OtlpExportState.Resolve();

        state.MetricExportIntervalMilliseconds.ShouldBeNull();
    }

    [Fact]
    public async Task MetricExportIntervalSet_ResolvesToParsedMilliseconds()
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");
        Environment.SetEnvironmentVariable(IntervalVar, "1234");

        var state = OtlpExportState.Resolve();

        state.MetricExportIntervalMilliseconds.ShouldBe(1234);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12.5")]
    public async Task MetricExportIntervalMalformed_FallsBackToNull_RatherThanThrowing(string value)
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");
        Environment.SetEnvironmentVariable(IntervalVar, value);

        var state = OtlpExportState.Resolve();

        state.MetricExportIntervalMilliseconds.ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task MetricExportIntervalNonPositive_FallsBackToNull(string value)
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");
        Environment.SetEnvironmentVariable(IntervalVar, value);

        var state = OtlpExportState.Resolve();

        state.MetricExportIntervalMilliseconds.ShouldBeNull();
    }

    // Same cleared-sources reason and same SDK constructor/registration path as the interval
    // above (OTEL_METRIC_EXPORT_TIMEOUT is parsed by the same PeriodicExportingMetricReaderOptions ctor).
    [Fact]
    public async Task NoMetricExportTimeout_ResolvesToNull()
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");

        var state = OtlpExportState.Resolve();

        state.MetricExportTimeoutMilliseconds.ShouldBeNull();
    }

    [Fact]
    public async Task MetricExportTimeoutSet_ResolvesToParsedMilliseconds()
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");
        Environment.SetEnvironmentVariable(TimeoutVar, "9876");

        var state = OtlpExportState.Resolve();

        state.MetricExportTimeoutMilliseconds.ShouldBe(9876);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12.5")]
    public async Task MetricExportTimeoutMalformed_FallsBackToNull_RatherThanThrowing(string value)
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");
        Environment.SetEnvironmentVariable(TimeoutVar, value);

        var state = OtlpExportState.Resolve();

        state.MetricExportTimeoutMilliseconds.ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task MetricExportTimeoutNonPositive_FallsBackToNull(string value)
    {
        using var _ = await AcquireCleanEnvAsync();
        Environment.SetEnvironmentVariable(EndpointVar, "http://localhost:4317");
        Environment.SetEnvironmentVariable(TimeoutVar, value);

        var state = OtlpExportState.Resolve();

        state.MetricExportTimeoutMilliseconds.ShouldBeNull();
    }

    // Serialized with the other env-var tests via TestData.EnvVarGate (process-global OTEL_* vars),
    // same mechanism as ServeRunnerTests.AcquireCleanEnvAsync.
    private const string IntervalVar = "OTEL_METRIC_EXPORT_INTERVAL";
    private const string TimeoutVar = "OTEL_METRIC_EXPORT_TIMEOUT";

    private static async Task<IDisposable> AcquireCleanEnvAsync()
    {
        await TestData.EnvVarGate.WaitAsync();
        var originalEndpoint = Environment.GetEnvironmentVariable(EndpointVar);
        var originalProtocol = Environment.GetEnvironmentVariable(ProtocolVar);
        var originalInterval = Environment.GetEnvironmentVariable(IntervalVar);
        var originalTimeout = Environment.GetEnvironmentVariable(TimeoutVar);
        Environment.SetEnvironmentVariable(EndpointVar, null);
        Environment.SetEnvironmentVariable(ProtocolVar, null);
        Environment.SetEnvironmentVariable(IntervalVar, null);
        Environment.SetEnvironmentVariable(TimeoutVar, null);
        return new EnvRestore(originalEndpoint, originalProtocol, originalInterval, originalTimeout);
    }

    private sealed class EnvRestore(
        string? originalEndpoint, string? originalProtocol, string? originalInterval, string? originalTimeout)
        : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EndpointVar, originalEndpoint);
            Environment.SetEnvironmentVariable(ProtocolVar, originalProtocol);
            Environment.SetEnvironmentVariable(IntervalVar, originalInterval);
            Environment.SetEnvironmentVariable(TimeoutVar, originalTimeout);
            TestData.EnvVarGate.Release();
        }
    }
}
