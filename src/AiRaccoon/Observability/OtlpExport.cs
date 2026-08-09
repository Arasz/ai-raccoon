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
                // Fixed product identity (ADR-0009): registered after CreateDefault()'s environment
                // detector, so it wins the resource merge even though OTEL_SERVICE_NAME reaches it too.
                .ConfigureResource(r => r.AddService(OtlpExportState.DefaultServiceName))
                .WithMetrics(m => m
                    .AddMeter("AiRaccoon.MemoryTools")
                    .AddMeter("AiRaccoon.PromotionQueue")
                    .AddMeter("System.Runtime")
                    .AddOtlpExporter(o => ConfigureExporter(o, state, MetricsSignalPath)))
                .WithTracing(t => t
                    // ASP.NET Core's unrecorded per-request Activity becomes the local parent for every
                    // tool-call span; the SDK's default ParentBased sampler drops those (ADR-0009).
                    .SetSampler(new AlwaysOnSampler())
                    .AddSource("AiRaccoon.MemoryTools")
                    .AddOtlpExporter(o => ConfigureExporter(o, state, TracesSignalPath)));

            return services;
        }
    }

    /// <summary>Endpoint/protocol/timeout stay explicit; other exporter config flows through the SDK's
    /// own OTEL_* parsing (docs/adr/0009-otlp-export.md).</summary>
    private static void ConfigureExporter(OtlpExporterOptions options, OtlpExportState state, string signalPath)
    {
        options.Endpoint = SignalEndpoint(state, signalPath);
        options.Protocol = IsHttpProtobuf(state.Protocol) ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        // Bounds a single unreachable-collector export attempt from hanging a detached serve
        // (docs/adr/0009-otlp-export.md).
        options.TimeoutMilliseconds = 5_000;
    }

    /// <summary>Resolves the per-exporter endpoint. gRPC carries the signal in the RPC method, so the
    /// base endpoint is used verbatim; http/protobuf appends the OTLP spec's signal path idempotently
    /// (no doubling on a base that already carries it or ends with '/'). Internal, not private, for direct unit testing.</summary>
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
