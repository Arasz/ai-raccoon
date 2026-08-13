using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Observability;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Logging;

namespace AiRaccoon.Hosting.Node;

/// <summary>
///     Composition root for `serve observability &lt;kind&gt;` (ADR 0008): dials the running
///     server's /observability endpoint, trusts the reported PID only when the server names
///     itself "ai-raccoon", and prints the requested monitoring command. Logs stay on stderr.
/// </summary>
public partial class ObservabilityRunner(IHttpClientFactory httpClientFactory) : IObservabilityRunner
{
    private const string ServerName = "ai-raccoon";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> RunAsync(CliInput cliInput, StandardStreams streams, CancellationToken ctx)
    {
        var options = new InfrastructureOptions
        {
            DataRoot = cliInput.Options.DataRoot,
            Scope = cliInput.Options.InstallScope,
            Quiet = cliInput.Options.Quiet
        };
        using var log = PreHostLogging.CreateLogger("ObservabilityRunner", options);
        var logger = log.Logger;

        var port = cliInput.ParsedCliArgs.GetValue(CliCommandTree.ObservabilityPortOption);
        var kind = ResolveKind(cliInput);

        ServerInfo? info;
        try
        {
            using var response = await httpClientFactory.CreateClient(nameof(ObservabilityRunner)).GetAsync($"http://127.0.0.1:{port}/observability", ctx);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Log.EndpointMissing(logger, port);
                await streams.WriteErrorLineAsync($"ai-raccoon: the server on port {port} does not expose /observability — upgrade it to read its PID");
                return ExitCode.NoServerRunning;
            }

            if (!response.IsSuccessStatusCode)
            {
                return await ReportForeignListenerAsync(logger, port, streams);
            }

            info = await response.Content.ReadFromJsonAsync<ServerInfo>(JsonOptions, ctx);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (!IsNothingListening(ex))
            {
                return await ReportForeignListenerAsync(logger, port, streams);
            }

            Log.NoServerListening(logger, port);
            await streams.WriteErrorLineAsync($"ai-raccoon: no server is listening on port {port} — start one with 'ai-raccoon serve --port {port}'");
            return ExitCode.NoServerRunning;
        }
        catch (JsonException)
        {
            return await ReportForeignListenerAsync(logger, port, streams);
        }

        if (info is null || info.Name != ServerName)
        {
            return await ReportForeignListenerAsync(logger, port, streams);
        }

        return kind switch
        {
            "counters" => await PrintAsync(streams, MonitoringCommandRenderer.RenderCounters(info.Pid)),
            "trace" => await PrintAsync(streams, MonitoringCommandRenderer.RenderTrace(info.Pid)),
            "pid" => await PrintAsync(streams, MonitoringCommandRenderer.RenderPid(info.Pid)),
            _ => await RunOtlpAsync(info, port, streams, logger)
        };
    }

    /// <summary>
    ///     observability's own --port and kind are read instance-based, never by name
    ///     (docs/plans/2026-08-06-http-serve-mode-plan.md R12): --port off the shared static Option
    ///     object, kind off the leaf command's own Argument instance.
    /// </summary>
    private static string ResolveKind(CliInput parsed)
    {
        var kindArgument = parsed.ParsedCliArgs.CommandResult.Command.Arguments.OfType<Argument<string>>().Single();
        return parsed.ParsedCliArgs.GetValue(kindArgument) ?? string.Empty;
    }

    private async Task<int> PrintAsync(StandardStreams streams, string line)
    {
        await streams.WriteOutputLineAsync(line);
        return ExitCode.Success;
    }

    private async Task<int> RunOtlpAsync(ServerInfo info, int port, StandardStreams streams, ILogger logger)
    {
        if (!info.Otlp.Enabled)
        {
            Log.OtlpNotEnabled(logger, port);
            await streams.WriteErrorLineAsync($"ai-raccoon: OTLP export is not enabled on the server on port {port} — set OTEL_EXPORTER_OTLP_ENDPOINT before starting it");
            return ExitCode.OtlpNotEnabled;
        }

        await streams.WriteOutputLineAsync(info.Otlp.Endpoint ?? "");
        await streams.WriteErrorLineAsync($"ai-raccoon: exporting OTLP over {info.Otlp.Protocol}");
        return ExitCode.Success;
    }

    private static async Task<int> ReportForeignListenerAsync(ILogger logger, int port, StandardStreams streams)
    {
        Log.ForeignListener(logger, port);
        await streams.WriteErrorLineAsync($"ai-raccoon: port {port} is in use by another process — it is not an ai-raccoon server");
        return ExitCode.PortInUse;
    }

    /// <summary>
    ///     True when nothing is listening: to connect itself was refused, or it never
    ///     completed. Anything else means a listener accepted us and then failed to behave like
    ///     ai-raccoon, which is a foreign listener.
    /// </summary>
    private static bool IsNothingListening(Exception exception)
    {
        if (exception is OperationCanceledException or TaskCanceledException)
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode is SocketError.ConnectionRefused
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.HostNotFound
                    or SocketError.TimedOut;
            }
        }

        return false;
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 620, Level = LogLevel.Warning, Message = "ai-raccoon: no server is listening on port {Port}")]
        public static partial void NoServerListening(ILogger logger, int port);

        [LoggerMessage(EventId = 621, Level = LogLevel.Warning, Message = "ai-raccoon: port {Port} is in use by another process")]
        public static partial void ForeignListener(ILogger logger, int port);

        [LoggerMessage(EventId = 622, Level = LogLevel.Warning, Message = "ai-raccoon: the server on port {Port} does not expose /observability")]
        public static partial void EndpointMissing(ILogger logger, int port);

        [LoggerMessage(EventId = 623, Level = LogLevel.Warning, Message = "ai-raccoon: OTLP export is not enabled on port {Port}")]
        public static partial void OtlpNotEnabled(ILogger logger, int port);
    }
}
