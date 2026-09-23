using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using CommunityToolkit.Diagnostics;
using ModelContextProtocol.Client;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
///     Owns every backend session the forwarder is handed, including the ones it swaps away: the
///     forwarder only ever replaces its reference. Re-opening re-runs the acquire, so a backend
///     that died is started again. The acquire is attach-or-start behind the identity proof
///     (ADR-0106): a proven listener on the configured port is attached to, nothing listening
///     starts one there, and anything that cannot prove gets a private ephemeral fallback — never
///     a secret byte. The proxy also owns the lifetime of that fallback child (owner ruling
///     2026-09-22): shutdown proves it again and stops it over the token-guarded /shutdown, while
///     a proven shared server is never touched. A fallback child that fails its own proof is sent
///     nothing and stopped through its process at once.
///     processPath is this process's own path (Environment.ProcessPath in production): the backend
///     is another ai-raccoon started as `serve`, so an unpackaged host cannot be it.
/// </summary>
public sealed partial class BackendSessions(
    IBackendLauncher backendLauncher,
    IIdentityProver identityProver,
    IServerProbe serverProbe,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    string? processPath,
    ServerConfig config) : IBackendSessions
{
    private const string BackendName = "ai-raccoon-backend";

    private readonly IIdentityProver _prover = identityProver;
    private readonly IServerProbe _probe = serverProbe;

    /// <summary>
    ///     What one attach-or-start attempt produced. A non-null <see cref="BackendResult.Url" /> is
    ///     always an endpoint that proved it holds this root's identity key; <see cref="Fallback" />
    ///     says the configured port could not be proven and a private ephemeral backend was started
    ///     instead.
    /// </summary>
    internal readonly record struct AcquireOutcome(BackendResult Result, bool Fallback, ProbeVerdict Verdict, IdentityProofFailure? ProofFailure);

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

    /// <summary>The /mcp endpoints of the private fallback backends this proxy started; empty when it attached to or started a shared server.</summary>
    private readonly List<Uri> _privateBackends = [];

    private readonly List<McpClient> _sessions = [];

    /// <summary>Makes disposal idempotent: a second dispose must not re-stop (or re-fail) anything.</summary>
    private bool _disposed;
    private readonly McpTokenFile _tokenFile = new(config.Options);

    /// <summary>The endpoint the last successful acquire returned; empty until one succeeds.</summary>
    public string Url { get; private set; } = string.Empty;


    internal static async Task<AcquireOutcome> AcquireSharedAsync(
        IServerProbe probe, IIdentityProver prover, IBackendLauncher launcher, string executable,
        ServerConfig config, TimeSpan? fallbackIdleTimeout, ILogger logger, CancellationToken ctx)
    {
        var endpoint = ServerProbe.EndpointFor(config.Port);
        var verdict = await probe.ProbeAsync(config.Port, ctx);
        if (verdict is ProbeVerdict.NotListening)
        {
            var started = await launcher.AcquireAsync(config.Port, executable,
                BackendLaunchArguments.ServeArguments(config), ctx);
            if (started.Url is null)
            {
                return new AcquireOutcome(started, false, verdict, null);
            }

            var failure = await prover.ProveAsync(new Uri(started.Url), ctx);
            if (failure is null)
            {
                return new AcquireOutcome(started, false, ProbeVerdict.Answered, null);
            }

            Log.FallbackOnUnprovenListener(logger, config.Port, failure);
            return await FallbackAsync(prover, launcher, executable, config, fallbackIdleTimeout, verdict, failure, ctx);
        }

        var proofFailure = await prover.ProveAsync(endpoint, ctx);
        if (proofFailure is null)
        {
            return new AcquireOutcome(new BackendResult(endpoint.ToString(), null), false, ProbeVerdict.Answered, null);
        }

        Log.FallbackOnUnprovenListener(logger, config.Port, proofFailure);
        return await FallbackAsync(prover, launcher, executable, config, fallbackIdleTimeout, verdict, proofFailure, ctx);
    }

    /// <summary>
    ///     Reuses this proxy's private fallback when it still proves, so a reopen never starts a
    ///     second child; otherwise (or with none) runs the normal <see cref="AcquireSharedAsync" />.
    /// </summary>
    internal static async Task<AcquireOutcome> ReuseOrAcquireAsync(
        Uri? privateBackend, IServerProbe probe, IIdentityProver prover, IBackendLauncher launcher, string executable,
        ServerConfig config, TimeSpan? fallbackIdleTimeout, ILogger logger, CancellationToken ctx)
    {
        if (privateBackend is not null && await prover.ProveAsync(privateBackend, ctx) is null)
        {
            return new AcquireOutcome(new BackendResult(privateBackend.ToString(), null), true, ProbeVerdict.Answered, null);
        }

        return await AcquireSharedAsync(probe, prover, launcher, executable, config, fallbackIdleTimeout, logger, ctx);
    }

    private static async Task<AcquireOutcome> FallbackAsync(
        IIdentityProver prover, IBackendLauncher launcher, string executable, ServerConfig config,
        TimeSpan? fallbackIdleTimeout, ProbeVerdict verdict, IdentityProofFailure? proofFailure, CancellationToken ctx)
    {
        var result = await launcher.StartPrivateAsync(executable,
            BackendLaunchArguments.PrivateServeArguments(config, fallbackIdleTimeout), ctx);
        if (result.Url is null)
        {
            return new AcquireOutcome(result, true, verdict, proofFailure);
        }

        var failure = await prover.ProveAsync(new Uri(result.Url), ctx);
        if (failure is null)
        {
            return new AcquireOutcome(result, true, verdict, proofFailure);
        }

        // The child cannot be sent the token, so /shutdown is closed to it; the proxy spawned it and
        // holds its process, so it stops it now rather than leaving it to its idle timeout.
        await BackendLauncher.StopChildAsync(result.Child);
        return new AcquireOutcome(result with { Url = null, Child = null }, true, verdict, failure);
    }

    public async Task<McpClient> OpenAsync(string? revision, CancellationToken ctx)
    {
        var acquired = await AcquireBackend(ctx);
        if (acquired.Result.Url is null)
        {
            var reason = acquired.Fallback
                ? $"the listener on port {config.Port} did not prove it serves this data root ({acquired.ProofFailure?.ToString() ?? "no key"}) and no private backend could be started (serve exit {acquired.Result.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})"
                : $"no MCP backend at {ServerProbe.EndpointFor(config.Port)} (serve exit {acquired.Result.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})";
            throw new BackendUnavailableException(Unavailable(
                reason + (acquired.Result.ServeStderr is { } stderr ? $" — stderr: {stderr}" : string.Empty)));
        }

        if (acquired.Fallback)
        {
            // The proxy owns this child's lifetime (K1a): recorded here — before any later failure
            // can skip the rest of this method — so shutdown stops it even when the open fails.
            // A proven listener, and the shared instance started on the configured port, are never
            // recorded: they serve other clients too.
            var endpoint = new Uri(acquired.Result.Url);
            if (!_privateBackends.Contains(endpoint))
            {
                _privateBackends.Add(endpoint);
            }
        }

        var token = _tokenFile.Read() ?? throw new BackendUnavailableException(Unavailable(
            acquired.Fallback
                ? $"the private backend at {acquired.Result.Url} is listening but {_tokenFile.Path} holds no token"
                : $"the backend at {acquired.Result.Url} is listening but {_tokenFile.Path} holds no token — a serve on another data root may own port {config.Port}"));

        Url = acquired.Result.Url;
        var session = await OpenSessionAsync(new Uri(acquired.Result.Url), token, revision, ctx);
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
    ///     Stops every private fallback backend this proxy started, so none outlives it (owner
    ///     ruling 2026-09-22): the token-guarded /shutdown is the product's own stop path, and the
    ///     URL is the one that child printed. The proof runs first (ADR-0106 F3): a listener that
    ///     cannot prove it serves this root is sent nothing and reported as not stopped. A backend
    ///     that cannot be stopped is reported and left to its idle timeout — this never kills a
    ///     process. A shared server is never touched: it serves other clients too.
    /// </summary>
    private async ValueTask StopPrivateBackendsAsync()
    {
        foreach (var endpoint in _privateBackends)
        {
            // F3/ADR-0106: prove before every token-bearing request, this one included. A listener
            // that cannot prove at stop time is sent nothing and reported as not stopped.
            if (await _prover.ProveAsync(endpoint, CancellationToken.None) is { } failure)
            {
                Log.PrivateBackendNotProved(_logger, endpoint, failure);
                continue;
            }

            if (await RequestStopAsync(endpoint, _tokenFile.Read()))
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

    private async Task<AcquireOutcome> AcquireBackend(CancellationToken ctx)
    {
        var executable = BackendLaunchArguments.Executable(processPath) ?? throw new BackendUnavailableException(
            Unavailable(BackendLaunchArguments.UnavailableExecutableMessage(processPath, config)));

        BankPresenceGuard.EnsureExists(config.Options);

        try
        {
            // ADR-0106: a proven listener on the configured port is attached to; nothing listening
            // starts one there; anything else (unproven, unanswered) gets a private fallback — but
            // only after the challenge, so no token byte rides before a proof.
            // No lock: reopens are serialised by the forwarder's gate, and the startup open runs
            // before the forwarder exists.
            return await ReuseOrAcquireAsync(_privateBackends.LastOrDefault(), _probe, _prover, backendLauncher,
                executable, config, fallbackIdleTimeout: null, _logger, ctx);
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
        [LoggerMessage(EventId = 690, Level = LogLevel.Warning,
            Message = "ai-raccoon: the listener on port {Port} did not prove it serves this data root ({Reason}); starting a private backend on an ephemeral port instead — stop the listener on port {Port} to reuse the shared server")]
        public static partial void FallbackOnUnprovenListener(ILogger logger, int port, IdentityProofFailure? reason);

        [LoggerMessage(EventId = 691, Level = LogLevel.Warning,
            Message = "ai-raccoon: the private backend at {Url} no longer proves it serves this data root ({Reason}); sending it nothing and leaving it to its idle timeout — stop it yourself if it must go now")]
        public static partial void PrivateBackendNotProved(ILogger logger, Uri url, IdentityProofFailure? reason);

        [LoggerMessage(EventId = 688, Level = LogLevel.Information,
            Message = "ai-raccoon: the private backend at {Url} stopped with this proxy — it was started for this proxy only")]
        public static partial void PrivateBackendStopped(ILogger logger, Uri url);

        [LoggerMessage(EventId = 689, Level = LogLevel.Warning,
            Message = "ai-raccoon: the private backend at {Url} did not stop within {Bound}; leaving it to its idle timeout")]
        public static partial void PrivateBackendDidNotStop(ILogger logger, Uri url, TimeSpan bound);
    }
}
