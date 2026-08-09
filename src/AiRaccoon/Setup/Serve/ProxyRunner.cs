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

        Log.ProxyReady(logger, backends.Url);
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

    /// <summary>Opens an MCP session on the backend endpoint, presenting the loopback token on every request.</summary>
    internal static Task<McpClient> OpenBackendAsync(Uri endpoint, string token, HttpClient httpClient,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        Guard.IsNotNullOrWhiteSpace(token);
        return McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = BackendName,
                    Endpoint = endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { [McpTokenGate.HeaderName] = token }
                },
                httpClient, loggerFactory, false),
            cancellationToken: cancellationToken);
    }

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
        private readonly McpTokenFile _tokenFile = new(config.Options.DataRoot);

        /// <summary>The endpoint the last successful acquire returned; empty until one succeeds.</summary>
        public string Url { get; private set; } = string.Empty;

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
                throw new BackendUnavailableException(Unavailable(
                    $"no MCP backend at {ServerProbe.EndpointFor(config.Port)} (serve exit " +
                    $"{acquired.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})"));
            }

            // Read after the acquire, never before: the token is minted strictly ahead of the bind,
            // so a port that answers guarantees the file is on disk. The proxy never mints
            // (docs/plans/2026-08-09-mcp-loopback-token-flow.md).
            var token = _tokenFile.Read() ?? throw new BackendUnavailableException(Unavailable(
                $"the backend at {acquired.Url} is listening but {_tokenFile.Path} holds no token — " +
                $"a serve on another data root may own port {config.Port}"));

            Url = acquired.Url;
            var session = await OpenSessionAsync(new Uri(acquired.Url), token, cancellationToken);
            lock (_sessions)
            {
                _sessions.Add(session);
            }

            return session;
        }

        /// <summary>A refused session is a diagnosable failure, not an unhandled crash on the client's stdio.</summary>
        private async Task<McpClient> OpenSessionAsync(Uri endpoint, string token, CancellationToken cancellationToken)
        {
            try
            {
                return await OpenBackendAsync(endpoint, token, _httpClient, loggerFactory, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BackendUnavailableException(Unavailable(
                    $"the backend at {endpoint} would not open a session ({ex.Message}) — " +
                    $"check that {_tokenFile.Path} holds the token it minted"));
            }
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

    /// <summary>Every way the backend can be unusable ends on the same line, with the same escape hatch.</summary>
    private static string Unavailable(string reason) =>
        $"ai-raccoon: {reason}; to serve in-process instead, run: ai-raccoon --transport stdio";

    internal static partial class Log
    {
        [LoggerMessage(EventId = 630, Level = LogLevel.Debug, Message = "ai-raccoon: proxying stdio to {Url}")]
        public static partial void ProxyReady(ILogger logger, string url);
    }
}
