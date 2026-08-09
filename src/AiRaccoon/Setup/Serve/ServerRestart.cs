using System.Net;
using System.Text.Json;
using AiRaccoon.Observability;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Setup.Serve;

/// <summary>How a restart attempt ended; only Nothing and Stopped let `serve` go on to bind.</summary>
internal enum RestartOutcome
{
    /// <summary>Nothing was listening — a restart is a plain start.</summary>
    Nothing,

    /// <summary>The server stopped and the port freed.</summary>
    Stopped,

    /// <summary>Something is listening but does not identify as an ai-raccoon server.</summary>
    Foreign,

    /// <summary>No token to present, so nothing was asked to stop.</summary>
    NoToken,

    /// <summary>The server rejected our token — it serves another data root.</summary>
    Refused,

    /// <summary>The server has no /shutdown endpoint; it is too old to be cycled.</summary>
    Unsupported,

    /// <summary>The shutdown was accepted but the port was still in use at the bound.</summary>
    TimedOut
}

/// <summary>What the restart attempt ended as, plus whatever the server said about itself.</summary>
internal readonly record struct RestartResult(RestartOutcome Outcome, int? Pid = null, string? Version = null);

/// <summary>
///     Cycles the ai-raccoon server on a loopback port for `serve --restart` (ADR-0022): identify
///     it, ask it to stop over the token-guarded /shutdown, then wait for the port to free. Never
///     signals or kills a process.
/// </summary>
internal sealed partial class ServerRestart
{
    /// <summary>Stands in for the version in operator lines when the server reports none —
    /// every build predating ADR-0022 does.</summary>
    public const string UnknownVersion = "(version not reported)";

    /// <summary>How long the port is given to free once the shutdown is accepted: twice the
    /// window a stopping host has to drain, so a full drain is never mistaken for a hang.</summary>
    public static readonly TimeSpan PortFreeWithin = ShutdownEndpoint.DrainWindow * 2;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Redirects are not followed: .NET carries custom headers like the token across
    /// them, and a listener that redirects has not identified itself either way.</summary>
    private static readonly HttpClient HttpClient =
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = RequestTimeout };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger;
    private readonly TimeSpan _portFreeWithin;
    private readonly ServerProbe _probe;
    private readonly TimeProvider _timeProvider;

    public ServerRestart(ServerProbe probe, ILogger logger, TimeSpan? portFreeWithin = null,
        TimeProvider? timeProvider = null)
    {
        Guard.IsNotNull(probe);
        Guard.IsNotNull(logger);
        _probe = probe;
        _logger = logger;
        _portFreeWithin = portFreeWithin ?? PortFreeWithin;
        _timeProvider = timeProvider ?? TimeProvider.System; // test seam: fake clock for the bound
        Guard.IsGreaterThan(_portFreeWithin, TimeSpan.Zero);
    }

    /// <summary>Stops whatever ai-raccoon server owns <paramref name="port" /> and waits for it to let go.</summary>
    public async Task<RestartResult> CycleAsync(int port, McpTokenFile tokenFile, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(tokenFile);

        if (!await _probe.RespondsAsync(port, cancellationToken))
        {
            return new RestartResult(RestartOutcome.Nothing);
        }

        // Identity first: an unidentified listener is never sent a shutdown.
        if (await IdentifyAsync(port, cancellationToken) is not { Name: "ai-raccoon" } info)
        {
            Log.Foreign(_logger, port);
            return new RestartResult(RestartOutcome.Foreign);
        }

        var found = new RestartResult(RestartOutcome.Stopped, info.Pid, info.Version);
        if (tokenFile.Read() is not { } token)
        {
            return found with { Outcome = RestartOutcome.NoToken };
        }

        Log.Stopping(_logger, port, info.Pid, info.Version ?? UnknownVersion);
        var status = await RequestShutdownAsync(port, token, cancellationToken);
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                Log.Refused(_logger, port, tokenFile.Path);
                return found with { Outcome = RestartOutcome.Refused };
            case HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed:
                Log.Unsupported(_logger, port, info.Version ?? UnknownVersion);
                return found with { Outcome = RestartOutcome.Unsupported };
        }

        if (!await WaitForPortToFreeAsync(port, cancellationToken))
        {
            Log.TimedOut(_logger, port, info.Pid, _portFreeWithin);
            return found with { Outcome = RestartOutcome.TimedOut };
        }

        Log.Stopped(_logger, port, info.Pid);
        return found;
    }

    /// <summary>The server's own account of itself, or null when the listener will not identify.</summary>
    private static async Task<ServerInfo?> IdentifyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync($"http://127.0.0.1:{port}/observability", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ServerInfo>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    ///     The status /shutdown answered. A connection that dies mid-request counts as accepted:
    ///     the server may have gone before it could flush, and the port poll is the real verdict.
    /// </summary>
    private static async Task<HttpStatusCode> RequestShutdownAsync(int port, string token,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"http://127.0.0.1:{port}{ShutdownEndpoint.Path}");
            request.Headers.Add(McpTokenGate.HeaderName, token);
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            return response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                       or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return HttpStatusCode.Accepted;
        }
    }

    /// <summary>True when the port stopped answering within the bound.</summary>
    private async Task<bool> WaitForPortToFreeAsync(int port, CancellationToken cancellationToken)
    {
        using var bound = new CancellationTokenSource(_portFreeWithin, _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bound.Token);
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        try
        {
            do
            {
                if (!await _probe.RespondsAsync(port, waiting.Token))
                {
                    return true;
                }
            }
            while (await timer.WaitForNextTickAsync(waiting.Token));
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The bound expired, not the caller's token: a miss, not a cancellation.
        }

        return false;
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 650, Level = LogLevel.Information,
            Message = "ai-raccoon: asking the server on port {Port} to stop (pid {Pid}, {Version})")]
        public static partial void Stopping(ILogger logger, int port, int pid, string version);

        [LoggerMessage(EventId = 651, Level = LogLevel.Information,
            Message = "ai-raccoon: the server on port {Port} (pid {Pid}) stopped and the port is free")]
        public static partial void Stopped(ILogger logger, int port, int pid);

        [LoggerMessage(EventId = 652, Level = LogLevel.Error,
            Message = "ai-raccoon: the server on port {Port} refused the token in {TokenPath}")]
        public static partial void Refused(ILogger logger, int port, string tokenPath);

        [LoggerMessage(EventId = 653, Level = LogLevel.Error,
            Message = "ai-raccoon: the server on port {Port} (pid {Pid}) still holds it {Bound} after accepting the shutdown")]
        public static partial void TimedOut(ILogger logger, int port, int pid, TimeSpan bound);

        [LoggerMessage(EventId = 654, Level = LogLevel.Error,
            Message = "ai-raccoon: the ai-raccoon {Version} on port {Port} has no /shutdown endpoint")]
        public static partial void Unsupported(ILogger logger, int port, string version);

        [LoggerMessage(EventId = 655, Level = LogLevel.Error,
            Message = "ai-raccoon: the listener on port {Port} does not identify as an ai-raccoon server")]
        public static partial void Foreign(ILogger logger, int port);
    }
}
