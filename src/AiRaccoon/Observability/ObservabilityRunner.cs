using System.CommandLine;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AiRaccoon.Setup.Cli;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AiRaccoon.Observability;

/// <summary>
///     Composition root for `serve observability &lt;kind&gt;` (ADR 0008): dials the running
///     server's /observability endpoint, trusts the reported PID only when the server names
///     itself "ai-raccoon", and prints the requested monitoring command. Logs stay on stderr.
/// </summary>
internal static partial class ObservabilityRunner
{
    private const string ServerName = "ai-raccoon";

    // ServeRunner's own bind-race probe uses 1s (same-process, same-machine); this dials a
    // possibly-foreign process over a real HTTP round trip, so it gets a bit more headroom
    // before being treated as "nothing is listening".
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Client = new() { Timeout = RequestTimeout };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(CliParseResult parsed, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));
        var logger = loggerFactory.CreateLogger("ObservabilityRunner");

        var port = parsed.ParseResult.GetValue(CliCommandTree.ObservabilityPortOption);
        var kind = ResolveKind(parsed);

        ServerInfo? info;
        try
        {
            using var response = await Client.GetAsync($"http://127.0.0.1:{port}/observability", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Log.EndpointMissing(logger, port);
                await stderr.WriteLineAsync($"ai-raccoon: the server on port {port} does not expose /observability — upgrade it to read its PID");
                return ExitCode.NoServerRunning;
            }

            if (!response.IsSuccessStatusCode)
            {
                return await ReportForeignListenerAsync(logger, port, stderr);
            }

            info = await response.Content.ReadFromJsonAsync<ServerInfo>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (IsResetByAnExistingListener(ex))
            {
                return await ReportForeignListenerAsync(logger, port, stderr);
            }

            Log.NoServerListening(logger, port);
            await stderr.WriteLineAsync($"ai-raccoon: no server is listening on port {port} — start one with 'ai-raccoon serve --port {port}'");
            return ExitCode.NoServerRunning;
        }
        catch (JsonException)
        {
            return await ReportForeignListenerAsync(logger, port, stderr);
        }

        if (info is null || info.Name != ServerName)
        {
            return await ReportForeignListenerAsync(logger, port, stderr);
        }

        return kind switch
        {
            "counters" => await PrintAsync(stdout, MonitoringCommandRenderer.RenderCounters(info.Pid)),
            "trace" => await PrintAsync(stdout, MonitoringCommandRenderer.RenderTrace(info.Pid)),
            "pid" => await PrintAsync(stdout, MonitoringCommandRenderer.RenderPid(info.Pid)),
            _ => await RunOtlpAsync(info, port, stdout, stderr, logger)
        };
    }

    /// <summary>observability's own --port and kind are read instance-based (R12): --port off
    /// the shared static Option object, kind off the Argument instance actually parsed onto
    /// the leaf command — it has no static field, so name-based lookup is avoided by fetching
    /// it from that command's own Arguments instead of searching the whole parse tree by name.</summary>
    private static string ResolveKind(CliParseResult parsed)
    {
        var kindArgument = parsed.ParseResult.CommandResult.Command.Arguments.OfType<Argument<string>>().Single();
        return parsed.ParseResult.GetValue(kindArgument) ?? string.Empty;
    }

    private static async Task<int> PrintAsync(TextWriter stdout, string line)
    {
        await stdout.WriteLineAsync(line);
        return ExitCode.Success;
    }

    private static async Task<int> RunOtlpAsync(ServerInfo info, int port, TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        if (!info.Otlp.Enabled)
        {
            Log.OtlpNotEnabled(logger, port);
            await stderr.WriteLineAsync($"ai-raccoon: OTLP export is not enabled on the server on port {port} — set OTEL_EXPORTER_OTLP_ENDPOINT before starting it");
            return ExitCode.OtlpNotEnabled;
        }

        await stdout.WriteLineAsync(info.Otlp.Endpoint);
        await stderr.WriteLineAsync($"ai-raccoon: exporting OTLP over {info.Otlp.Protocol}");
        return ExitCode.Success;
    }

    private static async Task<int> ReportForeignListenerAsync(ILogger logger, int port, TextWriter stderr)
    {
        Log.ForeignListener(logger, port);
        await stderr.WriteLineAsync($"ai-raccoon: port {port} is in use by another process — it is not an ai-raccoon server");
        return ExitCode.PortInUse;
    }

    /// <summary>True when the exception chain shows an existing listener accepted the
    /// connection and then reset it (ECONNRESET) — as opposed to nobody listening
    /// (ECONNREFUSED) or a timeout, both of which mean "no server" rather than "a foreign
    /// one squatting the port".</summary>
    private static bool IsResetByAnExistingListener(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                return true;
            }
        }

        return false;
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 610, Level = LogLevel.Warning, Message = "ai-raccoon: no server is listening on port {Port}")]
        public static partial void NoServerListening(ILogger logger, int port);

        [LoggerMessage(EventId = 611, Level = LogLevel.Warning, Message = "ai-raccoon: port {Port} is in use by another process")]
        public static partial void ForeignListener(ILogger logger, int port);

        [LoggerMessage(EventId = 612, Level = LogLevel.Warning, Message = "ai-raccoon: the server on port {Port} does not expose /observability")]
        public static partial void EndpointMissing(ILogger logger, int port);

        [LoggerMessage(EventId = 613, Level = LogLevel.Warning, Message = "ai-raccoon: OTLP export is not enabled on port {Port}")]
        public static partial void OtlpNotEnabled(ILogger logger, int port);
    }
}
