using System.Globalization;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     The bare-launch composition root (docs/adr/0020-always-on-http-stdio-proxy.md): acquire one
///     HTTP backend and relay every stdio message to it. Resolves no key, opens no bank, loads no model.
/// </summary>
internal static partial class ProxyRunner
{
    private const string BackendName = "ai-raccoon-backend";

    /// <summary>Relays stdio traffic to the backend until the client disconnects; loud on failure, never falls back.</summary>
    public static async Task<int> RunAsync(ServerConfig config, TextWriter stderr, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(config);
        Guard.IsNotNull(stderr);

        using var loggerFactory = CreateLoggerFactory(config);
        var logger = loggerFactory.CreateLogger("ProxyRunner");
        await using var backends = new BackendSessions(config, logger, loggerFactory);

        McpClient backend;
        try
        {
            backend = await backends.OpenAsync(cancellationToken);
        }
        catch (BackendUnavailableException ex)
        {
            // Written, not logged: --quiet must not silence the one line that says why memory is gone.
            await stderr.WriteLineAsync(ex.Message);
            return ExitCode.ProxyBackendUnavailable;
        }

        // The backend's identity, not the proxy's: on the per-request-metadata protocol revisions
        // the SDK stamps the LOCAL ServerInfo over the relayed one.
        var options = new McpServerOptions { ServerInfo = backend.ServerInfo };
        // No tools and no prompts: an interception failure has to surface as an empty tool list,
        // never as a second server quietly opening the bank (ADR-0020).
        options.Filters.Message.IncomingFilters.Add(
            ProxyForwarder.Create(backend, async ct => await backends.OpenAsync(ct), logger));

        Log.ProxyReady(logger, backends.Url!);
        await using var server = McpServer.Create(
            new StdioServerTransport(options, loggerFactory), options, loggerFactory, null);
        await server.RunAsync(cancellationToken);
        return ExitCode.Success;
    }

    /// <summary>
    ///     The proxy's HTTP client onto the backend: no client-side timeout (a tool call is bounded by
    ///     the caller's cancellation, as it is in-process) and JSON-RPC errors restored.
    /// </summary>
    internal static HttpClient CreateBackendHttpClient() =>
        new(new JsonRpcErrorHandler { InnerHandler = new SocketsHttpHandler() })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    /// <summary>Opens an MCP session on the backend endpoint over the proxy's own client.</summary>
    internal static Task<McpClient> OpenBackendAsync(Uri endpoint, HttpClient httpClient,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = BackendName,
                    Endpoint = endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp
                    // AdditionalHeaders is where the loopback token goes once it lands
                    // (docs/plans/2026-08-09-mcp-loopback-token-flow.md).
                },
                httpClient, loggerFactory, false),
            cancellationToken: cancellationToken);

    /// <summary>Launch identity forwarded to the spawned backend; the launch flags precede the verb.</summary>
    internal static string[] ServeArguments(ServerConfig config)
    {
        var arguments = new List<string>
        {
            "--data-root", config.Options.DataRoot,
            "--install-scope", config.Options.Scope.ToString().ToLowerInvariant()
        };
        if (config.Options.Quiet)
        {
            arguments.Add("--quiet");
        }

        arguments.AddRange(["serve", "--port", config.Port.ToString(CultureInfo.InvariantCulture)]);
        return [.. arguments];
    }

    /// <summary>Every log line goes to stderr: stdout is the JSON-RPC channel.</summary>
    private static ILoggerFactory CreateLoggerFactory(ServerConfig config) =>
        LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.AddFilter("ModelContextProtocol", LogLevel.Warning);
            if (config.Options.Quiet)
            {
                builder.SetMinimumLevel(LogLevel.Warning);
            }
        });

    /// <summary>Raised when the backend can neither be reached nor started; carries the operator's line.</summary>
    private sealed class BackendUnavailableException(string message) : Exception(message);

    /// <summary>
    ///     Owns every backend session the forwarder is handed, including the ones it swaps away: the
    ///     forwarder only ever replaces its reference. Re-opening re-runs the acquire, so a backend
    ///     that died is started again.
    /// </summary>
    private sealed class BackendSessions(ServerConfig config, ILogger logger, ILoggerFactory loggerFactory)
        : IAsyncDisposable
    {
        private readonly HttpClient _httpClient = CreateBackendHttpClient();
        private readonly BackendLauncher _launcher = new(ServerProbe.ForLoopback(), logger);
        private readonly List<McpClient> _sessions = [];

        /// <summary>The endpoint the last successful acquire returned.</summary>
        public string? Url { get; private set; }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in Snapshot())
            {
                await session.DisposeAsync();
            }

            _httpClient.Dispose();
        }

        public async Task<McpClient> OpenAsync(CancellationToken cancellationToken)
        {
            var acquired = await _launcher.AcquireAsync(config.Port, Executable(), ServeArguments(config),
                cancellationToken);
            if (acquired.Url is null)
            {
                throw new BackendUnavailableException(UnavailableMessage(config.Port, acquired.ServeExitCode));
            }

            Url = acquired.Url;
            var session = await OpenBackendAsync(new Uri(acquired.Url), _httpClient, loggerFactory, cancellationToken);
            lock (_sessions)
            {
                _sessions.Add(session);
            }

            return session;
        }

        private McpClient[] Snapshot()
        {
            lock (_sessions)
            {
                return [.. _sessions];
            }
        }
    }

    /// <summary>This very binary: the backend is another ai-raccoon, started as `serve`.</summary>
    private static string Executable() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("the running executable path is unknown");

    private static string UnavailableMessage(int port, int? serveExitCode) =>
        $"ai-raccoon: no MCP backend at {ServerProbe.EndpointFor(port)} (serve exit " +
        $"{serveExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}); " +
        "to serve in-process instead, run: ai-raccoon --transport stdio";

    internal static partial class Log
    {
        [LoggerMessage(EventId = 630, Level = LogLevel.Debug, Message = "ai-raccoon: proxying stdio to {Url}")]
        public static partial void ProxyReady(ILogger logger, string url);
    }
}
