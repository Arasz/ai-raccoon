using AiRaccoon.Observability;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>Resolves OTLP export state from OTEL_* environment variables; see ADR-0009.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class OtlpExportStateTests
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    [Fact]
    public async Task NoEndpointSet_IsDisabled()
    {
        await using var env = await AcquireCleanEnvAsync();

        var state = OtlpExportState.Resolve();

        state.ShouldBe(new OtlpExportState(false, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceEndpoint_IsDisabled(string endpoint)
    {
        await using var env = await AcquireCleanEnvAsync(endpoint);

        var state = OtlpExportState.Resolve();

        state.ShouldBe(new OtlpExportState(false, null, null));
    }

    [Fact]
    public async Task EndpointSet_IsEnabled_AndDefaultsToGrpc()
    {
        await using var env = await AcquireCleanEnvAsync("http://localhost:4317");

        var state = OtlpExportState.Resolve();

        state.ShouldBe(new OtlpExportState(true, "http://localhost:4317", "grpc"));
    }

    [Fact]
    public async Task ProtocolSet_IsReported()
    {
        await using var env = await AcquireCleanEnvAsync("http://localhost:4318", "http/protobuf");

        var state = OtlpExportState.Resolve();

        state.ShouldBe(new OtlpExportState(true, "http://localhost:4318", "http/protobuf"));
    }

    // A4: no scheme means `new Uri(...)` throws UriFormatException inside the exporter-configuration
    // delegate, which runs inside app.StartAsync — the MCP server dies at boot. Resolve() must
    // catch this at the source instead of letting the bad string reach the SDK.
    [Fact]
    public async Task MalformedEndpoint_MissingScheme_DisablesExportWithAReason()
    {
        await using var env = await AcquireCleanEnvAsync("127.0.0.1:4317");

        var state = OtlpExportState.Resolve();

        state.Enabled.ShouldBeFalse();
        state.InvalidEndpointReason.ShouldNotBeNullOrWhiteSpace();
    }

    // A5: `new Uri("localhost:4318")` *succeeds* with Scheme="localhost" — UriKind.Absolute alone
    // does not catch this. A naive "assert no exception" test passes today because the bug is
    // silent; asserting the reason is what makes this check able to fail.
    [Fact]
    public async Task MalformedEndpoint_NonHttpScheme_DisablesExportWithAReason()
    {
        await using var env = await AcquireCleanEnvAsync("localhost:4318");

        var state = OtlpExportState.Resolve();

        state.Enabled.ShouldBeFalse();
        state.InvalidEndpointReason.ShouldNotBeNullOrWhiteSpace();
    }

    // Serialized with the other env-var tests via EnvScope, which takes TestData.EnvVarGate
    // (the OTEL_* vars are process-global).
    private static ValueTask<EnvScope> AcquireCleanEnvAsync(string? endpoint = null, string? protocol = null) =>
        EnvScope.AcquireAsync(TestContext.Current.CancellationToken, (EndpointVar, endpoint), (ProtocolVar, protocol));
}
