namespace AiRaccoon.Observability;

/// <summary>OTLP export state read from OTEL_EXPORTER_OTLP_ENDPOINT/_PROTOCOL (ADR-0009). Everything
/// else the SDK needs it now reads itself from OTEL_*-prefixed environment variables via config;
/// this type only carries what <see cref="OtlpExport"/> sets explicitly and what the CLI reports.</summary>
public sealed record OtlpExportState(bool Enabled, string? Endpoint, string? Protocol)
{
    /// <summary>Fixed service.name for every export; OTEL_SERVICE_NAME cannot override it (ADR-0009).
    /// Without it the SDK falls back to "unknown_service:&lt;process&gt;", which is what collectors
    /// actually displayed.</summary>
    public const string DefaultServiceName = "ai-raccoon";

    /// <summary>Per-export timeout ceiling: bounds a single unreachable-collector export attempt
    /// from hanging a detached serve (ADR-0009).</summary>
    public const int ExportTimeoutMilliseconds = 5_000;

    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string DefaultProtocol = "grpc";

    /// <summary>Reads OTEL_EXPORTER_OTLP_ENDPOINT/OTEL_EXPORTER_OTLP_PROTOCOL; disabled unless
    /// the endpoint is set.</summary>
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
