using AiRaccoon.Hosting.Common;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
///     The bare-launch composition root (docs/adr/0020-always-on-http-stdio-proxy.md): acquire one
///     HTTP backend and relay every stdio message to it. Resolves no key, opens no bank, loads no model.
/// </summary>
public partial class ProxyRunner(IProxyForwarder proxyForwarder, IBackendLauncher backendLauncher, IServerProbe serverProbe, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILogger<ProxyRunner> logger) : IProxyRunner
{
    public async Task<int> RunAsync(ServerConfig serverConfig, StandardStreams streams, string? processPath, CancellationToken ctx)
    {
        if (serverConfig.Port is < 1 or > 65535)
        {
            await streams.WriteErrorLineAsync(Undialable(serverConfig.Port));
            return ErrorCode.Usage.UndialablePort;
        }

        // The verifier is bound to this launch's resolved root, exactly like the token reader below:
        // reading it from a DI singleton would tie the proof to whichever root registered first.
        var prover = new IdentityProver(serverConfig.Options, httpClientFactory);
        await using var backendSessions = new BackendSessions(backendLauncher, prover, serverProbe, httpClientFactory, loggerFactory, processPath, serverConfig,
            File.Exists, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("PATH"));

        McpClient backend;
        try
        {
            backend = await backendSessions.OpenAsync(null, ctx);
        }
        catch (BackendUnavailableException ex)
        {
            await streams.WriteErrorLineAsync(ex.Message);
            return ex.Code;
        }
        catch (BankMissingException ex)
        {
            await streams.WriteErrorLineAsync(ex.Message);
            return ErrorCode.Bank.NoBank;
        }

        var options = new McpServerOptions { ServerInfo = backend.ServerInfo };

        options.Filters.Message.IncomingFilters.Add(proxyForwarder.Create(backend, backendSessions));

        Log.ProxyReady(logger, backendSessions.Url);
        await using var server = McpServer.Create(new StdioServerTransport(options, loggerFactory), options, loggerFactory);
        await server.RunAsync(ctx);
        return ErrorCode.Ok.Success;
    }


    /// <summary>The line a port the proxy cannot dial ends on; names the supported random-port path.</summary>
    private static string Undialable(int port) =>
        $"ai-raccoon: the proxy cannot dial --port {port}: expected 1-65535, and 0 means \"any free port\"; pass a fixed --port, or run: ai-raccoon serve --port 0";


    internal static partial class Log
    {
        [LoggerMessage(EventId = 630, Level = LogLevel.Debug, Message = "ai-raccoon: proxying stdio to {Url}")]
        public static partial void ProxyReady(ILogger logger, string url);
    }
}

/// <summary>Raised when the backend can neither be reached nor started; carries the operator's line and the code naming the case.</summary>
public sealed class BackendUnavailableException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
