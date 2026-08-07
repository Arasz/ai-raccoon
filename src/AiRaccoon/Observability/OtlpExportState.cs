using System.Globalization;

namespace AiRaccoon.Observability;

/// <summary>OTLP export configuration read straight from OTEL_* environment variables; see ADR 0009.</summary>
public sealed record OtlpExportState(
    bool Enabled,
    string? Endpoint,
    string? Protocol,
    int? MetricExportIntervalMilliseconds = null,
    int? MetricExportTimeoutMilliseconds = null)
{
    /// <summary>Identifies this process to a collector. Without it the SDK falls back to
    /// "unknown_service:&lt;process&gt;", which is what collectors actually displayed.</summary>
    public const string DefaultServiceName = "ai-raccoon";

    /// <summary>service.name on the exported resource; OTEL_SERVICE_NAME overrides it.</summary>
    public string ServiceName { get; init; } = DefaultServiceName;

    private const string EndpointVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string MetricExportIntervalVar = "OTEL_METRIC_EXPORT_INTERVAL";
    private const string MetricExportTimeoutVar = "OTEL_METRIC_EXPORT_TIMEOUT";
    private const string ServiceNameVar = "OTEL_SERVICE_NAME";
    private const string DefaultProtocol = "grpc";

    /// <summary>Reads OTEL_EXPORTER_OTLP_ENDPOINT/OTEL_EXPORTER_OTLP_PROTOCOL/OTEL_METRIC_EXPORT_INTERVAL/
    /// OTEL_METRIC_EXPORT_TIMEOUT; disabled unless the endpoint is set.</summary>
    public static OtlpExportState Resolve()
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointVar);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new OtlpExportState(false, null, null);
        }

        var protocol = Environment.GetEnvironmentVariable(ProtocolVar);
        var serviceName = Environment.GetEnvironmentVariable(ServiceNameVar);
        return new OtlpExportState(true, endpoint, string.IsNullOrWhiteSpace(protocol) ? DefaultProtocol : protocol,
            ResolvePositiveMilliseconds(MetricExportIntervalVar), ResolvePositiveMilliseconds(MetricExportTimeoutVar))
        {
            ServiceName = string.IsNullOrWhiteSpace(serviceName) ? DefaultServiceName : serviceName.Trim()
        };
    }

    /// <summary>Malformed or non-positive values fall back to null (the SDK's own default applies) —
    /// a bad env var must never prevent the server from starting.</summary>
    private static int? ResolvePositiveMilliseconds(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return null;
        }

        return parsed;
    }
}
