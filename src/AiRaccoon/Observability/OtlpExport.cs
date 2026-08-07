using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AiRaccoon.Observability;

/// <summary>Wires the OTel SDK for OTLP export (ADR 0009): opt-in on OTEL_EXPORTER_OTLP_ENDPOINT.</summary>
internal static class OtlpExport
{
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
                .WithMetrics(m => m
                    .AddMeter("AiRaccoon.MemoryTools")
                    .AddMeter("AiRaccoon.PromotionQueue")
                    .AddMeter("System.Runtime")
                    .AddOtlpExporter(o => ConfigureExporter(o, state)))
                .WithTracing(t => t
                    .AddSource("AiRaccoon.MemoryTools")
                    .AddOtlpExporter(o => ConfigureExporter(o, state)));

            return services;
        }
    }

    /// <summary>Explicit endpoint/protocol only (ADR 0009 hazard 1): the settings-clear ruling
    /// means the SDK's implicit OTEL_* IConfiguration binding never sees these values.</summary>
    private static void ConfigureExporter(OtlpExporterOptions options, OtlpExportState state)
    {
        options.Endpoint = new Uri(state.Endpoint!);
        options.Protocol = state.Protocol == "http/protobuf" ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        // Bounds a single unreachable-collector export attempt so a detached serve never
        // hangs on the SDK's 30s default (ADR 0009: measured against the SDK sources).
        options.TimeoutMilliseconds = 5_000;
    }
}
