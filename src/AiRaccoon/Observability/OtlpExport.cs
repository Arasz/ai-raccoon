using OpenTelemetry.Resources;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AiRaccoon.Observability;

/// <summary>Wires the OTel SDK for OTLP export (ADR 0009): opt-in on OTEL_EXPORTER_OTLP_ENDPOINT.</summary>
internal static class OtlpExport
{
    private const string TracesSignalPath = "/v1/traces";
    private const string MetricsSignalPath = "/v1/metrics";

    extension(IServiceCollection services)
    {
        /// <summary>No-op unless OTLP is enabled (ADR 0009): zero threads, zero sockets when unconfigured.
        /// resolvedState is a test seam — production callers always resolve from the environment.</summary>
        internal IServiceCollection AddOtlpExport(OtlpExportState? resolvedState = null)
        {
            var state = resolvedState ?? OtlpExportState.Resolve();
            if (!state.Enabled)
            {
                return services;
            }

            services.AddOpenTelemetry()
                // Explicit, for the same cleared-sources reason as the endpoint: the environment
                // detector behind the default resource resolves through DI's IConfiguration, so
                // OTEL_SERVICE_NAME never reaches it and collectors show "unknown_service:<process>".
                .ConfigureResource(r => r.AddService(state.ServiceName))
                .WithMetrics(m => m
                    .AddMeter("AiRaccoon.MemoryTools")
                    .AddMeter("AiRaccoon.PromotionQueue")
                    .AddMeter("System.Runtime")
                    .AddOtlpExporter((exporterOptions, readerOptions) =>
                    {
                        ConfigureExporter(exporterOptions, state, MetricsSignalPath);
                        ConfigureMetricReader(readerOptions, state);
                    }))
                .WithTracing(t => t
                    .AddSource("AiRaccoon.MemoryTools")
                    .AddOtlpExporter(o => ConfigureExporter(o, state, TracesSignalPath)));

            return services;
        }
    }

    /// <summary>Explicit endpoint/protocol only (ADR 0009 hazard 1): the settings-clear ruling
    /// means the SDK's implicit OTEL_* IConfiguration binding never sees these values, which
    /// also means the SDK's own per-signal path append (AppendSignalPathToEndpoint) never
    /// fires — <see cref="SignalEndpoint"/> reproduces it for http/protobuf.</summary>
    private static void ConfigureExporter(OtlpExporterOptions options, OtlpExportState state, string signalPath)
    {
        options.Endpoint = SignalEndpoint(state, signalPath);
        options.Protocol = IsHttpProtobuf(state.Protocol) ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        // Bounds a single unreachable-collector export attempt so a detached serve never
        // hangs on the SDK's 30s default (ADR 0009: measured against the SDK sources).
        options.TimeoutMilliseconds = 5_000;
    }

    /// <summary>Explicit OTEL_METRIC_EXPORT_INTERVAL/_TIMEOUT only, same reason as
    /// <see cref="ConfigureExporter"/>: the SDK core resolves PeriodicExportingMetricReaderOptions
    /// through DI's (cleared) IConfiguration, so its own env-var parsing never reaches the
    /// reader. Null values are left untouched — the SDK's own default applies.</summary>
    private static void ConfigureMetricReader(MetricReaderOptions options, OtlpExportState state)
    {
        if (state.MetricExportIntervalMilliseconds is { } interval)
        {
            options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval;
        }

        if (state.MetricExportTimeoutMilliseconds is { } timeout)
        {
            options.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = timeout;
        }
    }

    /// <summary>Resolves the per-exporter endpoint. gRPC carries the signal in the RPC method, so the
    /// base endpoint is used verbatim; http/protobuf requires the signal path per the OTLP spec, and is
    /// appended idempotently — an endpoint that already carries it, or a trailing slash on the base, is
    /// handled without doubling. Internal (not private) so it stays directly unit-testable.</summary>
    internal static Uri SignalEndpoint(OtlpExportState state, string signalPath)
    {
        var endpoint = state.Endpoint!;
        if (!IsHttpProtobuf(state.Protocol) || endpoint.EndsWith(signalPath, StringComparison.Ordinal))
        {
            return new Uri(endpoint);
        }

        return new Uri(endpoint.TrimEnd('/') + signalPath);
    }

    private static bool IsHttpProtobuf(string? protocol) =>
        string.Equals(protocol?.Trim(), "http/protobuf", StringComparison.OrdinalIgnoreCase);
}
