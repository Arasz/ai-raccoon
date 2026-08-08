namespace AiRaccoon.Observability;

/// <summary>OTLP export state read from OTEL_EXPORTER_OTLP_ENDPOINT/_PROTOCOL; see ADR 0009.
/// Everything else the OpenTelemetry SDK needs (headers, compression, resource attributes,
/// per-signal overrides, mTLS, sampler, metric export interval/timeout, ...) it now reads
/// itself from OTEL_*-prefixed environment variables via config (ADR 0009 2026-08-07 update) —
/// this type only carries what <see cref="OtlpExport"/> sets explicitly and what the
/// `serve observability otlp` CLI verb reports.</summary>
public sealed record OtlpExportState(bool Enabled, string? Endpoint, string? Protocol)
{
    /// <summary>Identifies this process to a collector. Without it the SDK falls back to
    /// "unknown_service:&lt;process&gt;", which is what collectors actually displayed.</summary>
    private const string DefaultServiceName = "ai-raccoon";

    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string ServiceNameVar = "OTEL_SERVICE_NAME";
    private const string DefaultProtocol = "grpc";

    /// <summary>service.name on the exported resource; OTEL_SERVICE_NAME overrides it.</summary>
    public string ServiceName { get; private init; } = DefaultServiceName;

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
        var serviceName = Environment.GetEnvironmentVariable(ServiceNameVar);
        return new OtlpExportState(true, endpoint, string.IsNullOrWhiteSpace(protocol) ? DefaultProtocol : protocol)
        {
            ServiceName = string.IsNullOrWhiteSpace(serviceName) ? DefaultServiceName : serviceName.Trim()
        };
    }
}
