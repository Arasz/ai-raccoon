using AiRaccoon.Hosting.Common;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
///     The bare-launch composition root (docs/adr/0020-always-on-http-stdio-proxy.md): acquire one
///     HTTP backend and relay every stdio message to it. Resolves no key, opens no bank, loads no model.
/// </summary>
public partial class ProxyRunner(IProxyForwarder proxyForwarder, IBackendLauncher backendLauncher, IHttpClientFactory httpClientFactory, ILogger<ProxyRunner> logger) : IProxyRunner
{
    public async Task<int> RunAsync(ServerConfig serverConfig, StandardStreams streams, string? processPath, CancellationToken ctx)
    {
        if (serverConfig.Port is < 1 or > 65535)
        {
            await streams.WriteErrorLineAsync(Undialable(serverConfig.Port));
            return ExitCode.ProxyBackendUnavailable;
        }

        var loggerFactory = CreateMcpLoggerFactory(serverConfig);
        await using var backendSessions = new BackendSessions(backendLauncher, httpClientFactory, loggerFactory, processPath, serverConfig);

        McpClient backend;
        try
        {
            backend = await backendSessions.OpenAsync(null, ctx);
        }
        catch (BackendUnavailableException ex)
        {
            await streams.WriteErrorLineAsync(ex.Message);
            return ExitCode.ProxyBackendUnavailable;
        }

        var options = new McpServerOptions { ServerInfo = backend.ServerInfo };

        options.Filters.Message.IncomingFilters.Add(proxyForwarder.Create(backend, backendSessions));

        Log.ProxyReady(logger, backendSessions.Url);
        await using var server = McpServer.Create(new StdioServerTransport(options, loggerFactory), options, loggerFactory);
        await server.RunAsync(ctx);
        return ExitCode.Success;
    }


    /// <summary>Every log line goes to stderr: stdout is the JSON-RPC channel.</summary>
    private static ILoggerFactory CreateMcpLoggerFactory(ServerConfig config) =>
        LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.AddFilter("ModelContextProtocol", LogLevel.Warning);
            if (config.Options.Quiet)
            {
                builder.SetMinimumLevel(LogLevel.Warning);
            }
        });

    /// <summary>The line a port the proxy cannot dial ends on; names the supported random-port path.</summary>
    private static string Undialable(int port) =>
        $"ai-raccoon: the proxy cannot dial --port {port}: expected 1-65535, and 0 means \"any free port\"; pass a fixed --port, or run: ai-raccoon serve --port 0";


    internal static partial class Log
    {
        [LoggerMessage(EventId = 630, Level = LogLevel.Debug, Message = "ai-raccoon: proxying stdio to {Url}")]
        public static partial void ProxyReady(ILogger logger, string url);
    }
}

/// <summary>Raised when the backend can neither be reached nor started; carries the operator's line.</summary>
public sealed class BackendUnavailableException(string message) : Exception(message);
