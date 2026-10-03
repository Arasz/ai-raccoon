using System.Net;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Observability;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Node;

/// <summary>
///     Cycles the ai-raccoon server on a loopback port for `serve --restart` (ADR-0022): prove it
///     holds this root's identity key (ADR-0106), identify it, ask it to stop over the token-guarded
///     /shutdown, then wait for the port to free. Never signals or kills a process. Nothing but the
///     probe and the challenge reaches a listener before it proves (D5), and everything after the
///     proof rides the connection it proved on.
/// </summary>
public sealed partial class ServerRestart : IServerRestart
{
    /// <summary>
    ///     Stands in for the version in operator lines when the server reports none —
    ///     every build predating ADR-0022 does.
    /// </summary>
    public const string UnknownVersion = "(version not reported)";

    private const string BaseUrl = "http://127.0.0.1";

    /// <summary>
    ///     How long the port is given to free once the shutdown is accepted: twice the
    ///     window a stopping host has to drain, so a full drain is never mistaken for a hang.
    /// </summary>
    public static readonly TimeSpan PortFreeWithin = ShutdownEndpoint.DrainWindow * 2;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Time the OS is given to release the listening socket after the probe stops
    /// answering, so the restart's own bind never loses the port to a TIME_WAIT hangover.</summary>
    private static readonly TimeSpan PortSettleGrace = TimeSpan.FromMilliseconds(100);


    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger _logger;
    private readonly TimeSpan _portFreeWithin;
    private readonly IIdentityProver _prover;
    private readonly IServerProbe _probe;
    private readonly TimeProvider _timeProvider;

    public ServerRestart(IServerProbe probe, TimeSpan portFreeWithin, TimeProvider timeProvider, IIdentityProver prover, ILogger<ServerRestart> logger)
    {
        _probe = probe;
        _logger = logger;
        _portFreeWithin = portFreeWithin;
        _timeProvider = timeProvider;
        _prover = prover;
        Guard.IsGreaterThan(_portFreeWithin, TimeSpan.Zero);
    }

    /// <summary>Stops whatever ai-raccoon server owns <paramref name="port" /> and waits for it to let go.</summary>
    public async Task<RestartResult> CycleAsync(int port, McpTokenFile tokenFile, CancellationToken ctx)
    {
        Guard.IsNotNull(tokenFile);

        if (RestartTransition.FromProbe(await _probe.ProbeAsync(port, ctx)) is { } settled)
        {
            if (settled is RestartOutcome.Unknown)
            {
                Log.ProbeUnanswered(_logger, port);
            }

            return new RestartResult(settled);
        }

        // ADR-0106 D5: an unproven listener receives only the probe and the challenge. The proof
        // comes first, and the identify read and the shutdown ride the connection it proved on,
        // so a server that lets go of the port in between takes the token's only route with it.
        using var channel = await _prover.ProveChannelAsync(ServerProbe.EndpointFor(port), ctx);
        if (channel.Client is not { } proven)
        {
            Log.Unproven(_logger, port, channel.Failure);
            return new RestartResult(RestartOutcome.Unproven, Reason: channel.Failure);
        }

        if (await IdentifyAsync(proven, port, ctx) is not { Name: ServerInfo.ServerName } info)
        {
            Log.Foreign(_logger, port);
            return new RestartResult(RestartOutcome.Foreign);
        }

        return await StopAsync(proven, port, tokenFile, info, ctx);
    }

    /// <summary>Asks the identified server to stop with the token and waits for its port to free; reports NoToken when there is no token to send.</summary>
    private async Task<RestartResult> StopAsync(HttpClient proven, int port, McpTokenFile tokenFile, ServerInfo info, CancellationToken ctx)
    {
        var found = new RestartResult(RestartOutcome.Stopped, info.Pid, info.Version);
        if (tokenFile.Read() is not { } token)
        {
            return found with { Outcome = RestartOutcome.NoToken };
        }

        Log.Stopping(_logger, port, info.Pid, info.Version ?? UnknownVersion);
        var status = await RequestShutdownAsync(proven, port, token, ctx);
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                Log.Refused(_logger, port, tokenFile.Path);
                return found with { Outcome = RestartOutcome.Refused };
            case HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed:
                Log.Unsupported(_logger, port, info.Version ?? UnknownVersion);
                return found with { Outcome = RestartOutcome.Unsupported };
        }

        if (!await WaitForPortToFreeAsync(port, ctx))
        {
            Log.TimedOut(_logger, port, info.Pid, _portFreeWithin);
            return found with { Outcome = RestartOutcome.TimedOut };
        }

        Log.Stopped(_logger, port, info.Pid);
        return found;
    }

    /// <summary>The server's own account of itself, or null when the listener will not identify.</summary>
    private static async Task<ServerInfo?> IdentifyAsync(HttpClient proven, int port, CancellationToken ctx)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ctx);
            bound.CancelAfter(RequestTimeout);
            using var response = await proven.GetAsync($"{BaseUrl}:{port}/observability", bound.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ServerInfo>(JsonOptions, bound.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       or OperationCanceledException && !ctx.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    ///     The status /shutdown answered. A connection that dies mid-request counts as accepted:
    ///     the server may have gone before it could flush, and the port poll is the real verdict. So
    ///     does a proven connection already closed, which is never re-dialled.
    /// </summary>
    private static async Task<HttpStatusCode> RequestShutdownAsync(HttpClient proven, int port, string token,
        CancellationToken ctx)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ctx);
            bound.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}:{port}{ShutdownEndpoint.Path}");
            request.Headers.Add(McpTokenGate.HeaderName, token);
            using var response = await proven.SendAsync(request, bound.Token);
            return response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                       or OperationCanceledException && !ctx.IsCancellationRequested)
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
                // Only a refused connection proves the port is free (docs/adr/0043). Treating any
                // non-answer as "freed" is the same conflation the pre-check had: a server that
                // accepts the shutdown, stops answering and keeps the socket open would be reported
                // as stopped, and the bind that follows would then blame a nonexistent second server
                // for taking the port.
                if (await _probe.ProbeAsync(port, waiting.Token) is not ProbeVerdict.NotListening)
                {
                    continue;
                }

                await Task.Delay(PortSettleGrace, _timeProvider, cancellationToken);
                return true;
            } while (await timer.WaitForNextTickAsync(waiting.Token));
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

        [LoggerMessage(EventId = 656, Level = LogLevel.Warning,
            Message = "ai-raccoon: port {Port} gave the probe no answer; nothing is asked to stop")]
        public static partial void ProbeUnanswered(ILogger logger, int port);

        [LoggerMessage(EventId = 657, Level = LogLevel.Warning,
            Message = "ai-raccoon: the listener on port {Port} did not prove it holds this data root's identity key ({Reason}); nothing is asked to stop")]
        public static partial void Unproven(ILogger logger, int port, IdentityProofFailure? reason);
    }
}
