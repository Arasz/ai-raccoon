using System.Globalization;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using CommunityToolkit.Diagnostics;
using ModelContextProtocol.Client;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
///     Owns every backend session the forwarder is handed, including the ones it swaps away: the
///     forwarder only ever replaces its reference. Re-opening re-runs the acquire, so a backend
///     that died is started again.
/// </summary>
public sealed class BackendSessions(IBackendLauncher backendLauncher, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ServerConfig config) : IBackendSessions
{
    private const string BackendName = "ai-raccoon-backend";

    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(BackendLauncher.BackendSessionClient);

    private readonly List<McpClient> _sessions = [];
    private readonly McpTokenFile _tokenFile = new(config.Options.DataRoot);

    /// <summary>The endpoint the last successful acquire returned; empty until one succeeds.</summary>
    public string Url { get; private set; } = string.Empty;


    public async Task<McpClient> OpenAsync(string? revision, CancellationToken ctx)
    {
        var acquired = await AcuireBackend(ctx);
        if (acquired.Url is null)
        {
            throw new BackendUnavailableException(Unavailable(
                $"no MCP backend at {ServerProbe.EndpointFor(config.Port)} (serve exit {acquired.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})"));
        }

        var token = _tokenFile.Read() ?? throw new BackendUnavailableException(Unavailable(
            $"the backend at {acquired.Url} is listening but {_tokenFile.Path} holds no token — a serve on another data root may own port {config.Port}"));

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
        foreach (var session in Snapshot())
        {
            await session.DisposeAsync();
        }

        _httpClient.Dispose();
    }

    private async Task<BackendResult> AcuireBackend(CancellationToken ctx)
    {
        var executable = BackendLaunchArguments.Executable() ?? throw new BackendUnavailableException(
            Unavailable(BackendLaunchArguments.UnavailableExecutableMessage(config)));

        BackendResult acquired;
        try
        {
            acquired = await backendLauncher.AcquireAsync(config.Port, executable, BackendLaunchArguments.ServeArguments(config),
                ctx);
        }
        catch (BackendStartException ex)
        {
            throw new BackendUnavailableException(Unavailable(ex.Message));
        }

        return acquired;
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

    /// <summary>Every way the backend can be unusable ends on the same line, with the same escape hatch.</summary>
    private static string Unavailable(string reason) => $"ai-raccoon: {reason}; to serve in-process instead, run: ai-raccoon --transport stdio";
}
