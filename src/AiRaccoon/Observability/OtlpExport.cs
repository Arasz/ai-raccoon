using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AiRaccoon.Observability;

/// <summary>Wires the OTel SDK for OTLP export (ADR 0009): opt-in on OTEL_EXPORTER_OTLP_ENDPOINT.</summary>
internal static partial class OtlpExport
{
    private const string TracesSignalPath = "/v1/traces";
    private const string MetricsSignalPath = "/v1/metrics";

    extension(IServiceCollection services)
    {
        /// <summary>
        ///     No-op unless OTLP is enabled (ADR 0009): zero threads, zero sockets when unconfigured.
        ///     resolvedState is a test seam — production callers always resolve from the environment.
        /// </summary>
        internal IServiceCollection AddOtlpExport(InfrastructureOptions options, OtlpExportState? resolvedState = null)
        {
            var state = resolvedState ?? OtlpExportState.Resolve();
            if (!state.Enabled)
            {
                if (state.InvalidEndpointReason is not null)
                {
                    // No DI logger yet (AddOtlpExport runs before builder.Build()) — PreHostLogging
                    // routes this through the same quiet/transport rules as the built host's own logger.
                    using var log = PreHostLogging.CreateLogger("OtlpExport", options);
                    Log.InvalidEndpoint(log.Logger, state.InvalidEndpointReason);
                }

                return services;
            }

            services.AddOpenTelemetry()
                // Fixed product identity (ADR-0009): registered after CreateDefault()'s environment
                // detector, so it wins the resource merge even though OTEL_SERVICE_NAME reaches it too.
                .ConfigureResource(r => r.AddService(OtlpExportState.DefaultServiceName, serviceVersion: ServerInfo.BinaryVersion))
                .WithMetrics(m => m
                    .AddMeter([.. OtlpNames.Meters])
                    .AddOtlpExporter(o => ConfigureExporter(o, state, MetricsSignalPath)))
                .WithTracing(t => t
                    // The request span is now recorded (ADR-0021), so ParentBased samples it and
                    // OTEL_TRACES_SAMPLER is live configuration again.
                    .AddSource([.. OtlpNames.Sources])
                    .AddOtlpExporter(o => ConfigureExporter(o, state, TracesSignalPath)));

            return services;
        }
    }

    /// <summary>
    ///     Endpoint/protocol/timeout stay explicit; other exporter config flows through the SDK's
    ///     own OTEL_* parsing (docs/adr/0009-otlp-export.md). Internal, not private, for direct unit testing.
    /// </summary>
    internal static void ConfigureExporter(OtlpExporterOptions options, OtlpExportState state, string signalPath)
    {
        options.Endpoint = SignalEndpoint(state, signalPath);
        options.Protocol = IsHttpProtobuf(state.Protocol) ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        options.TimeoutMilliseconds = OtlpExportState.ExportTimeoutMilliseconds;
    }

    /// <summary>
    ///     Resolves the per-exporter endpoint. gRPC carries the signal in the RPC method, so the
    ///     base endpoint is used verbatim; http/protobuf appends the OTLP spec's signal path idempotently
    ///     (no doubling on a base that already carries it or ends with '/'). Internal, not private, for direct unit testing.
    /// </summary>
    internal static Uri SignalEndpoint(OtlpExportState state, string signalPath)
    {
        var endpoint = state.Endpoint!;
        if (!IsHttpProtobuf(state.Protocol) || HasSignalPath(endpoint, signalPath))
        {
            return new Uri(endpoint);
        }

        return new Uri(endpoint.TrimEnd('/') + signalPath);
    }

    /// <summary>
    ///     Matches the SDK's own AppendPathIfNotPresent: the path form or the path-plus-slash
    ///     form, case-insensitively.
    /// </summary>
    private static bool HasSignalPath(string endpoint, string signalPath) =>
        endpoint.EndsWith(signalPath, StringComparison.OrdinalIgnoreCase) ||
        endpoint.EndsWith(signalPath + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpProtobuf(string? protocol) => string.Equals(protocol?.Trim(), "http/protobuf", StringComparison.OrdinalIgnoreCase);

    internal static partial class Log
    {
        [LoggerMessage(EventId = 640, Level = LogLevel.Warning, Message = "ai-raccoon: OTLP export disabled — {Reason}")]
        public static partial void InvalidEndpoint(ILogger logger, string reason);
    }
}
