namespace AiRaccoon.Observability;

/// <summary>OTLP export configuration read straight from OTEL_* environment variables; see ADR 0009.</summary>
public sealed record OtlpExportState(bool Enabled, string? Endpoint, string? Protocol)
{
    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string DefaultProtocol = "grpc";

    /// <summary>Reads OTEL_EXPORTER_OTLP_ENDPOINT/OTEL_EXPORTER_OTLP_PROTOCOL; disabled unless the endpoint is set.</summary>
    public static OtlpExportState Resolve()
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointVar);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new OtlpExportState(false, null, null);
        }

        var protocol = Environment.GetEnvironmentVariable(ProtocolVar);
        return new OtlpExportState(true, endpoint, string.IsNullOrWhiteSpace(protocol) ? DefaultProtocol : protocol);
    }
}
