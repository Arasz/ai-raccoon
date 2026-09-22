using System.Globalization;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using CommunityToolkit.Diagnostics;
using ModelContextProtocol.Client;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
///     Owns every backend session the forwarder is handed, including the ones it swaps away: the
///     forwarder only ever replaces its reference. Re-opening re-runs the acquire, so a backend
///     that died is started again. By default the acquire is a private spawn (F70/K1): the proxy
///     starts its own backend on an ephemeral port and never attaches to a pre-existing listener;
///     <see cref="ServerConfig.Attach" /> opts into the shared server. The proxy also owns the
///     lifetime of what it starts (owner ruling 2026-09-22): shutdown stops every private backend
///     over the token-guarded /shutdown, while an attached shared server is never touched.
///     processPath is this process's own path (Environment.ProcessPath in production): the backend
///     is another ai-raccoon started as `serve`, so an unpackaged host cannot be it.
/// </summary>
public sealed partial class BackendSessions(IBackendLauncher backendLauncher, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, string? processPath, ServerConfig config) : IBackendSessions
{
    private const string BackendName = "ai-raccoon-backend";

    /// <summary>How often the stop path re-checks that the private backend let go of its port.</summary>
    private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    ///     How long a private backend gets to stop once asked, before it is left to its idle
    ///     timeout: twice the drain window a stopping host takes, the same grace `serve --restart`
    ///     gives (ServerRestart.PortFreeWithin).
    /// </summary>
    private static readonly TimeSpan StopBound = ShutdownEndpoint.DrainWindow * 2;

    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(BackendLauncher.BackendSessionClient);

    private readonly ILogger _logger = loggerFactory.CreateLogger<BackendSessions>();

    /// <summary>The /mcp endpoints of the private backends this proxy started; empty under --attach.</summary>
    private readonly List<Uri> _privateBackends = [];

    private readonly List<McpClient> _sessions = [];

    /// <summary>Makes disposal idempotent: a second dispose must not re-stop (or re-fail) anything.</summary>
    private bool _disposed;
    private readonly McpTokenFile _tokenFile = new(config.Options.DataRoot);

    /// <summary>The endpoint the last successful acquire returned; empty until one succeeds.</summary>
    public string Url { get; private set; } = string.Empty;


    public async Task<McpClient> OpenAsync(string? revision, CancellationToken ctx)
    {
        var acquired = await AcquireBackend(ctx);
        if (acquired.Url is null)
        {
            var reason = config.Attach
                ? $"no MCP backend at {ServerProbe.EndpointFor(config.Port)} (serve exit {acquired.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})"
                : $"no MCP backend could be started on a private port (serve exit {acquired.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})";
            throw new BackendUnavailableException(Unavailable(
                reason + (acquired.ServeStderr is { } stderr ? $" — stderr: {stderr}" : string.Empty)));
        }

        if (!config.Attach)
        {
            // The proxy owns this child's lifetime: recorded here — before any later failure can
            // skip the rest of this method — so shutdown stops it even when the open fails.
            _privateBackends.Add(new Uri(acquired.Url));
        }

        var token = _tokenFile.Read() ?? throw new BackendUnavailableException(Unavailable(
            config.Attach
                ? $"the backend at {acquired.Url} is listening but {_tokenFile.Path} holds no token — a serve on another data root may own port {config.Port}"
                : $"the private backend at {acquired.Url} is listening but {_tokenFile.Path} holds no token"));

        Url = acquired.Url;
        var session = await OpenSessionAsync(new Uri(acquired.Url), token, revision, ctx);
        lock (_sessions)
        {
            _sessions.Add(session);
        }

        return session;
    }


    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var session in Snapshot())
        {
            await session.DisposeAsync();
        }

        await StopPrivateBackendsAsync();
        _httpClient.Dispose();
    }

    /// <summary>
    ///     Stops every private backend this proxy started, so none outlives it (owner ruling
    ///     2026-09-22): the token-guarded /shutdown is the product's own stop path, and the URL is
    ///     the one that child printed. A backend that cannot be stopped is reported and left to its
    ///     idle timeout — this never kills a process. An attached shared server is never touched:
    ///     it serves other clients too.
    /// </summary>
    private async ValueTask StopPrivateBackendsAsync()
    {
        var token = _privateBackends.Count > 0 ? _tokenFile.Read() : null;
        foreach (var endpoint in _privateBackends)
        {
            if (await RequestStopAsync(endpoint, token))
            {
                Log.PrivateBackendStopped(_logger, endpoint);
            }
            else
            {
                Log.PrivateBackendDidNotStop(_logger, endpoint, StopBound);
            }
        }
    }

    /// <summary>
    ///     Asks the backend to stop and waits until its port is provably free. True once the
    ///     connection is refused (ADR-0043: the one verdict that proves the port is free); false at
    ///     the bound, or when no token can authorize the stop.
    /// </summary>
    private async Task<bool> RequestStopAsync(Uri endpoint, string? token)
    {
        if (token is null)
        {
            return false;
        }

        using var bound = new CancellationTokenSource(StopBound);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ShutdownUriFor(endpoint));
            request.Headers.Add(McpTokenGate.HeaderName, token);
            using var response = await _httpClient.SendAsync(request, bound.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // A backend already gone cannot answer the request; the probe below is the verdict.
        }

        var probe = new ServerProbe(_httpClient);
        try
        {
            while (!bound.IsCancellationRequested)
            {
                if (await probe.ProbeAsync(endpoint, bound.Token) is ProbeVerdict.NotListening)
                {
                    return true;
                }

                await Task.Delay(StopPollInterval, bound.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // The bound expired mid-wait: reported as not-stopped rather than thrown.
        }

        return false;
    }

    private static Uri ShutdownUriFor(Uri endpoint) =>
        new($"{endpoint.Scheme}://{endpoint.Authority}{ShutdownEndpoint.Path}");

    private async Task<BackendResult> AcquireBackend(CancellationToken ctx)
    {
        var executable = BackendLaunchArguments.Executable(processPath) ?? throw new BackendUnavailableException(
            Unavailable(BackendLaunchArguments.UnavailableExecutableMessage(processPath, config)));

        try
        {
            // F70/K1: the default starts a private backend whose URL only this child can report;
            // the legacy attach-or-start path is reachable only through the explicit opt-in.
            return config.Attach
                ? await backendLauncher.AcquireAsync(config.Port, executable, BackendLaunchArguments.ServeArguments(config), ctx)
                : await backendLauncher.StartPrivateAsync(executable, BackendLaunchArguments.PrivateServeArguments(config), ctx);
        }
        catch (BackendStartException ex)
        {
            throw new BackendUnavailableException(Unavailable(ex.Message));
        }
    }

    /// <summary>A refused session is a diagnosable failure, not an unhandled crash on the client's stdio.</summary>
    private async Task<McpClient> OpenSessionAsync(Uri endpoint, string token, string? revision,
        CancellationToken ctx)
    {
        try
        {
            return await OpenBackendAsync(endpoint, token, revision, _httpClient, loggerFactory,
                ctx);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BackendUnavailableException(Unavailable(
                $"the backend at {endpoint} would not open a session ({ex.Message}) — check that {_tokenFile.Path} holds the token it minted"));
        }
    }

    private McpClient[] Snapshot()
    {
        lock (_sessions)
        {
            return [.. _sessions];
        }
    }

    /// <summary>
    ///     Opens an MCP session on the backend endpoint, presenting the loopback token on every request.
    ///     revision pins the protocol the session negotiates; null leaves the choice to the SDK.
    /// </summary>
    private static Task<McpClient> OpenBackendAsync(Uri endpoint, string token, string? revision,
        HttpClient httpClient, ILoggerFactory loggerFactory, CancellationToken ctx)
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
                httpClient, loggerFactory),
            new McpClientOptions { ProtocolVersion = revision },
            cancellationToken: ctx);
    }

    /// <summary>Every way the backend can be unusable ends on the same line, with the same serve pointer.</summary>
    private static string Unavailable(string reason) => $"ai-raccoon: {reason}; no in-process fallback exists — start the backend first: ai-raccoon serve --port <port>";

    internal static partial class Log
    {
        [LoggerMessage(EventId = 688, Level = LogLevel.Information,
            Message = "ai-raccoon: the private backend at {Url} stopped with this proxy — it was started for this proxy only")]
        public static partial void PrivateBackendStopped(ILogger logger, Uri url);

        [LoggerMessage(EventId = 689, Level = LogLevel.Warning,
            Message = "ai-raccoon: the private backend at {Url} did not stop within {Bound}; leaving it to its idle timeout")]
        public static partial void PrivateBackendDidNotStop(ILogger logger, Uri url, TimeSpan bound);
    }
}
