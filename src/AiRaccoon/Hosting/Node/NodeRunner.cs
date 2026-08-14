using System.Net.Sockets;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Setup.Cli.Render;
using AiRaccoon.Setup.Extensions;
using AiRaccoon.Setup.Models;
using CommunityToolkit.Diagnostics;
using Microsoft.AspNetCore.Connections;

namespace AiRaccoon.Hosting.Node;

/// <summary>
///     Starts the HTTP/S based MCP service
/// </summary>
internal partial class NodeRunner(
    IServerRestart serverRestart,
    IServerProbe serverProbe,
    ISqliteConnectionFactory connectionFactory,
    IEncryptionKeyResolver encryptionKeyResolver,
    IEmbeddingAvailability
        embeddingAvailability,
    ILogger<NodeRunner>
        logger) : INodeRunner
{
    public async Task<int> RunAsync(CliInput cliInput, StandardStreams streams, CancellationToken ctx)
    {
        var options = cliInput.ParsedCliArgs.GetServeOptions().Options?.Node ?? ThrowHelper.ThrowArgumentException<NodeCliOptions>("Missing serve options");
        var descriptor = new NodeLaunchDescriptor(cliInput.ServerConfig)
        {
            Source = options,
            Port = options.Port,
            Transport = McpTransport.Http,
            IdleTimeout = IdleTimeoutParser.TryParse(options.IdleTimeout, out var idleTimeout) ? idleTimeout : DefaultOptions.IdleTimeout,
            Restarting = options.Restart,
            TokenFile = new McpTokenFile(cliInput.ServerConfig.Options.DataRoot)
        };
        WarnOnNonHttpTransport(cliInput.ServerConfig, cliInput.Options.IsTransportExplicit, streams);

        var result = await RestartServer(descriptor, streams, ctx);
        if (!result.IsSuccess)
        {
            return result.Code;
        }

        var tokenFile = descriptor.TokenFile;
        if (await tokenFile.EnsureAsync(ctx) is not { } mcpToken)
        {
            Log.McpTokenUnavailable(logger, tokenFile.Path);
            await streams.WriteErrorLineAsync($"ai-raccoon: cannot read or create the MCP token at {tokenFile.Path} — check its permissions, or remove it and start serve again");
            return ExitCode.McpTokenUnavailable;
        }

        Log.McpTokenReady(logger, tokenFile.Path);

        return await StartHttpMcpServer(descriptor with { Token = mcpToken }, streams, ctx);
    }

    private async Task<ServerRestartResult> RestartServer(NodeLaunchDescriptor descriptor, StandardStreams streams, CancellationToken ctx)
    {
        if (!await serverProbe.RespondsAsync(descriptor.Port, ctx))
        {
            return ServerRestartResult.Success;
        }

        if (!descriptor.Restarting)
        {
            return ServerRestartResult.Failure(await ReportAttachedAsync(descriptor, streams));
        }

        var restartResult = await serverRestart.CycleAsync(descriptor.Port, descriptor.TokenFile, ctx);
        if (RestartRefusal(descriptor, restartResult) is { } refusal)
        {
            await streams.WriteErrorLineAsync(refusal.Message);
            return ServerRestartResult.Failure(refusal.Code);
        }

        return ServerRestartResult.Success;
    }


    private async Task<int> StartHttpMcpServer(NodeLaunchDescriptor descriptor, StandardStreams streams, CancellationToken ctx)
    {
        var serverHost = McpServerSetup.CreateWebHost(descriptor.ToServerConfig());
        try
        {
            var probeResolvingEncryptionKey = await encryptionKeyResolver.ProbeResolvingEncryptionKeyAsync(ctx);
            if (!probeResolvingEncryptionKey.IsSuccess)
            {
                return ExitCode.FailedToOpenEncryptedBank;
            }

            var probeUsingEncryptionKey = await connectionFactory.ProbeUsingEncryptionKey(probeResolvingEncryptionKey.Key.Passphrase, ctx);
            if (!probeUsingEncryptionKey.IsCorrectKey)
            {
                return ExitCode.FailedToOpenEncryptedBank;
            }

            await embeddingAvailability.EnsureEmbeddingAvailabilityAsync(ctx);

            await serverHost.StartAsync(ctx);
            await EmitBoundUrl(descriptor, streams, serverHost);
            await serverHost.WaitForShutdownAsync(ctx);
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            var port = descriptor.Port;

            if (await serverProbe.RespondsAsync(port, ctx))
            {
                if (!descriptor.Restarting)
                {
                    return await ReportAttachedAsync(descriptor, streams);
                }

                Log.RestartLostThePort(logger, port);
                await streams.WriteErrorLineAsync(
                    $"ai-raccoon: restart on port {port} did not take — another server took the port while this one was starting; check it with 'ai-raccoon serve observability pid --port {port}'");
                return ExitCode.RestartLostThePort;
            }

            Log.PortInUse(logger, port);
            await streams.WriteErrorLineAsync($"ai-raccoon: port {port} is in use — pass --port 0 for a random port, or free the port");
            return ExitCode.PortInUse;
        }
        finally
        {
            if (serverHost is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }

        return ExitCode.Success;
    }

    private async Task EmitBoundUrl(NodeLaunchDescriptor descriptor, StandardStreams streams, WebApplication serverHost)
    {
        var boundUrl = $"{serverHost.Urls.First().TrimEnd('/')}/mcp";
        var boundPort = new Uri(boundUrl).Port;

        Log.ServeListening(logger, boundUrl);

        await streams.RenderUrlForInput(boundUrl, boundPort, descriptor.Source.McpEntry, descriptor.Source.Format);
    }

    private void WarnOnNonHttpTransport(ServerConfig serverConfig, bool transportExplicit, StandardStreams streams)
    {
        var selected = serverConfig.Transport;
        if (!transportExplicit || selected == McpTransport.Http)
        {
            return;
        }

        Log.IgnoringTransport(logger, selected);
        streams.WriteErrorLine($"ai-raccoon: serve ignoring --transport {selected}; serve always uses http");
    }

    /// <summary>
    ///     True when the exception chain marks the bind failure as address-in-use
    ///     (Kestrel surfaces Microsoft.AspNetCore.Connections.AddressInUseException or an
    ///     IOException wrapping a SocketException with AddressAlreadyInUse).
    /// </summary>
    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AddressInUseException:
                case SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }:
                    return true;
            }
        }

        return exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The operator line and exit code for a restart that cannot go ahead, or null when
    ///     `serve` may bind: nothing was listening, or the server stopped and freed the port.
    /// </summary>
    private static (string Message, int Code)? RestartRefusal(NodeLaunchDescriptor descriptor, RestartResult result) =>
        result.Outcome switch
        {
            RestartOutcome.Nothing or RestartOutcome.Stopped => null,
            RestartOutcome.Foreign => (
                $"ai-raccoon: port {descriptor.Port} is held by a listener that does not identify as an ai-raccoon server — stop it yourself, or serve on another port",
                ExitCode.PortInUse),
            RestartOutcome.NoToken => (
                $"ai-raccoon: cannot restart the server on port {descriptor.Port}: {descriptor.TokenFile.Path} holds no token, so it cannot be asked to stop — it may serve another data root; stop it " +
                $"yourself, or" +
                $" serve on" +
                $" another port",
                ExitCode.RestartNoToken),
            RestartOutcome.Refused => (
                $"ai-raccoon: cannot restart the server on port {descriptor.Port}: it refused the token in {descriptor.TokenFile.Path} — it serves another data root; stop it yourself, or serve on another port",
                ExitCode.RestartTokenRefused),
            RestartOutcome.Unsupported => (
                $"ai-raccoon: cannot restart the server on port {descriptor.Port}: the ai-raccoon {result.Version ?? ServerRestart.UnknownVersion} serving it (pid {result.Pid}) is too old to be asked to stop — stop it yourself, then run serve again",
                ExitCode.RestartUnsupportedServer),
            _ => (
                $"ai-raccoon: restart on port {descriptor.Port} timed out: the server (pid {result.Pid}) accepted the shutdown but still held the port {ServerRestart.PortFreeWithin.TotalSeconds:0}s later — stop it yourself, then run serve again",
                ExitCode.RestartTimedOut)
        };

    private async Task<int> ReportAttachedAsync(NodeLaunchDescriptor descriptor, StandardStreams streams)
    {
        // UX-F10: this process never opens the owning server's bank, so it cannot confirm the
        // two match -- naming the bank *this* invocation asked for at least makes a
        // --data-root mismatch visible instead of a silent takeover.
        var requestedBankPath = SqliteConnectionFactory.BankPathFor(descriptor.LaunchConfig.Options);
        Log.AttachedToExistingServer(logger, descriptor.Url, requestedBankPath);
        await streams.WriteErrorLineAsync(
            $"ai-raccoon: attached to the server already listening on {descriptor.Url} — it may not be serving {requestedBankPath}; this process never opened that bank to check. To serve it here, stop the other server first or free the port (--port 0)");
        await streams.RenderUrlForInput(descriptor.Url, descriptor.Port, descriptor.Source.McpEntry, descriptor.Source.Format);
        return ExitCode.Success;
    }

    private sealed record ServerRestartResult(bool IsSuccess, int Code)
    {
        public static readonly ServerRestartResult Success = new(true, 200);

        public static ServerRestartResult Failure(int code) => new(false, code);
    }

    private sealed record NodeLaunchDescriptor(ServerConfig LaunchConfig)
    {
        public string Url => $"http://127.0.0.1:{Port}/mcp";
        public required NodeCliOptions Source { get; init; }
        public required int Port { get; init; }
        public required McpTransport Transport { get; init; }
        public required TimeSpan IdleTimeout { get; set; }
        public required bool Restarting { get; init; }
        public required McpTokenFile TokenFile { get; init; }
        public string Token { get; init; } = "";

        public ServerConfig ToServerConfig() =>
            new(Port, Transport, LaunchConfig.Options, IdleTimeout)
            {
                McpToken = Token
            };
    }


    internal static partial class Log
    {
        [LoggerMessage(EventId = 601, Level = LogLevel.Debug, Message = "ai-raccoon: serve listening on {Url}")]
        public static partial void ServeListening(ILogger logger, string url);

        [LoggerMessage(EventId = 602, Level = LogLevel.Warning, Message = "ai-raccoon: serve ignoring --transport {Transport}; serve always uses http")]
        public static partial void IgnoringTransport(ILogger logger, McpTransport transport);

        [LoggerMessage(EventId = 603, Level = LogLevel.Error, Message = "ai-raccoon: port {Port} is in use — pass --port 0 for a random port, or free the port")]
        public static partial void PortInUse(ILogger logger, int port);

        [LoggerMessage(EventId = 605, Level = LogLevel.Information, Message = "ai-raccoon: attached to the server already listening on {Url} — this process asked for {RequestedBankPath}")]
        public static partial void AttachedToExistingServer(ILogger logger, string url, string requestedBankPath);

        [LoggerMessage(EventId = 606, Level = LogLevel.Debug, Message = "ai-raccoon: /mcp is guarded by the token in {TokenPath}")]
        public static partial void McpTokenReady(ILogger logger, string tokenPath);

        [LoggerMessage(EventId = 607, Level = LogLevel.Error,
            Message = "ai-raccoon: cannot read or create the MCP token at {TokenPath} — check its permissions, or remove it and start serve again")]
        public static partial void McpTokenUnavailable(ILogger logger, string tokenPath);

        [LoggerMessage(EventId = 608, Level = LogLevel.Error,
            Message = "ai-raccoon: restart on port {Port} did not take — another server took the port while this one was starting")]
        public static partial void RestartLostThePort(ILogger logger, int port);
    }
}
