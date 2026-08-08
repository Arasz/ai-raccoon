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
                // Fixed product identity (ADR 0009 2026-08-07 update): registered after
                // CreateDefault()'s environment detector, so it wins the resource merge even
                // though OTEL_SERVICE_NAME now reaches that detector too.
                .ConfigureResource(r => r.AddService(OtlpExportState.DefaultServiceName))
                .WithMetrics(m => m
                    .AddMeter("AiRaccoon.MemoryTools")
                    .AddMeter("AiRaccoon.PromotionQueue")
                    .AddMeter("System.Runtime")
                    .AddOtlpExporter(o => ConfigureExporter(o, state, MetricsSignalPath)))
                .WithTracing(t => t
                    .AddSource("AiRaccoon.MemoryTools")
                    .AddOtlpExporter(o => ConfigureExporter(o, state, TracesSignalPath)));

            return services;
        }
    }

    /// <summary>Endpoint/protocol/timeout stay explicit even though the SDK now reads OTEL_* from
    /// config again (ADR 0009 "Configuration channel" 2026-08-07 update): the http/protobuf signal
    /// path composition (<see cref="SignalEndpoint"/>) and the 5s timeout ceiling are product
    /// decisions, not values the config channel alone would produce. Headers, compression, resource
    /// attributes, per-signal overrides, mTLS, and sampler now flow through the SDK's own OTEL_*
    /// parsing instead.</summary>
    private static void ConfigureExporter(OtlpExporterOptions options, OtlpExportState state, string signalPath)
    {
        options.Endpoint = SignalEndpoint(state, signalPath);
        options.Protocol = IsHttpProtobuf(state.Protocol) ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        // Bounds a single unreachable-collector export attempt so a detached serve never
        // hangs on the SDK's 30s default (ADR 0009: measured against the SDK sources).
        options.TimeoutMilliseconds = 5_000;
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
