using System.Globalization;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using CommunityToolkit.Diagnostics;
using ModelContextProtocol.Client;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
/// Owns MCP sessions; every open proves the configured endpoint before sending credentials.
/// Only confirmed connection refusal permits a shared backend start; shared lifetime belongs to IdleWatchdog.
/// </summary>
public sealed class BackendSessions(
    IBackendLauncher backendLauncher,
    IIdentityProver identityProver,
    IServerProbe serverProbe,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    string? processPath,
    ServerConfig config,
    Func<string, bool> fileExists,
    string? userProfileDirectory,
    string? pathVariable,
    string? currentProcessPath) : IBackendSessions
{
    private const string BackendName = "ai-raccoon-backend";

    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(BackendLauncher.BackendSessionClient);

    private readonly ILogger _logger = loggerFactory.CreateLogger<BackendSessions>();

    private readonly IServerProbe _probe = serverProbe;

    private readonly IIdentityProver _prover = identityProver;

    private readonly List<McpClient> _sessions = [];
    private readonly McpTokenFile _tokenFile = new(config.Options);

    /// <summary>Makes disposal idempotent: a second dispose must not re-stop (or re-fail) anything.</summary>
    private bool _disposed;

    /// <summary>The endpoint the last successful acquire returned; empty until one succeeds.</summary>
    public string Url { get; private set; } = string.Empty;

    public async Task<McpClient> OpenAsync(string? revision, CancellationToken ctx)
    {
        var acquired = await AcquireBackend(ctx);
        if (acquired.Result.Url is null)
        {
            if (acquired.ProofFailure is { } failure)
            {
                throw new BackendUnavailableException(ErrorCode.Server.Unproven, Refusal(config.Port, failure));
            }

            throw new BackendUnavailableException(ErrorCode.Reach.BackendUnavailable, Unavailable(
                $"no MCP backend at {ServerProbe.EndpointFor(config.Port)} (serve exit {acquired.Result.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})" +
                (acquired.Result.ServeStderr is { } stderr ? $" — stderr: {stderr}" : string.Empty)));
        }

        // ADR-0107 PC.4: acquired.Result.Url is only ever non-null once identity is already proven
        // for both attach and start, so "another data root" never applies
        // here — RefusalReason (e.g. a chmod-700 remedy) is the real, actionable cause when set.
        var token = _tokenFile.Read() ?? throw new BackendUnavailableException(ErrorCode.Server.NoToken, Unavailable(
            $"the backend at {acquired.Result.Url} is listening but has no usable token for this data root " +
            $"({_tokenFile.RefusalReason ?? $"{_tokenFile.Path} holds no token yet"})"));

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

        _httpClient.Dispose();
    }


    internal static async Task<AcquireOutcome> AcquireSharedAsync(
        IServerProbe probe, IIdentityProver prover, IBackendLauncher launcher, string executable,
        ServerConfig config, ILogger logger, CancellationToken ctx)
    {
        var endpoint = ServerProbe.EndpointFor(config.Port);
        var verdict = await probe.ProbeAsync(config.Port, ctx);
        ctx.ThrowIfCancellationRequested();
        if (verdict is ProbeVerdict.NotListening)
        {
            var started = await launcher.AcquireAsync(config.Port, executable,
                BackendLaunchArguments.ServeArguments(config), ctx);
            ctx.ThrowIfCancellationRequested();
            if (started.Url is null)
            {
                return new AcquireOutcome(started, verdict, null);
            }

            var failure = await prover.ProveAsync(new Uri(started.Url), ctx);
            ctx.ThrowIfCancellationRequested();
            if (failure is null)
            {
                return new AcquireOutcome(started, ProbeVerdict.Answered, null);
            }

            return new AcquireOutcome(started with { Url = null }, verdict, failure);
        }

        var proofFailure = await prover.ProveAsync(endpoint, ctx);
        ctx.ThrowIfCancellationRequested();
        if (proofFailure is null)
        {
            return new AcquireOutcome(new BackendResult(endpoint.ToString(), null), verdict, null);
        }

        return new AcquireOutcome(new BackendResult(null, null), verdict, proofFailure);
    }

    private async Task<AcquireOutcome> AcquireBackend(CancellationToken ctx)
    {
        var own = BackendLaunchArguments.Executable(processPath) ?? throw new BackendUnavailableException(
            ErrorCode.Reach.AutoStartUnsupported, Unavailable(BackendLaunchArguments.UnavailableExecutableMessage(processPath, config)));
        var executable = BackendLaunchArguments.ResolveExecutable(own, currentProcessPath, _logger, fileExists, userProfileDirectory, pathVariable);

        BankPresenceGuard.EnsureExists(config.Options);

        try
        {
            return await AcquireSharedAsync(_probe, _prover, backendLauncher, executable, config, _logger, ctx);
        }
        catch (BackendStartException ex)
        {
            throw new BackendUnavailableException(ErrorCode.Reach.StartFailed, Unavailable(ex.Message));
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
            throw new BackendUnavailableException(ErrorCode.Server.SessionRefused, Unavailable(
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

    internal static string Refusal(int port, IdentityProofFailure failure) =>
        $"ai-raccoon: the listener at {ServerProbe.EndpointFor(port)} is unproven ({IdentityProof.RefusalText(failure)}); no extra backend was started — check the data root and identity key, or explicitly stop the conflicting server before retrying";

    /// <summary>A URL is returned only after the endpoint proves this root's identity.</summary>
    internal readonly record struct AcquireOutcome(BackendResult Result, ProbeVerdict Verdict, IdentityProofFailure? ProofFailure);
}
