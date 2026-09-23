using System.Net.Sockets;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Embedding;
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
    IIdentityProver identityProver,
    ISqliteConnectionFactory connectionFactory,
    IEncryptionKeyResolver encryptionKeyResolver,
    IEmbeddingAvailability
        embeddingAvailability,
    IEntryEmbedder entryEmbedder,
    ICodeEmbedder codeEmbedder,
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
            IdleTimeout = IdleTimeoutParser.TryParse(options.IdleTimeout, out var idleTimeout) ? idleTimeout : DefaultOptions.IdleTimeout,
            Restarting = options.Restart,
            TokenFile = new McpTokenFile(cliInput.ServerConfig.Options)
        };
        WarnOnNonHttpTransport(cliInput.ServerConfig, cliInput.Options.IsTransportExplicit, streams);

        var preBind = await RestartServer(descriptor, streams, ctx);
        if (preBind.ExitNow is { } exitNow)
        {
            return exitNow;
        }

        var tokenFile = descriptor.TokenFile;
        if (await tokenFile.EnsureAsync(ctx) is not { } mcpToken)
        {
            Log.McpTokenUnavailable(logger, tokenFile.Path);
            await streams.WriteErrorLineAsync($"ai-raccoon: {tokenFile.RefusalReason ?? $"cannot read or create the MCP token at {tokenFile.Path} — check its permissions, or remove it and start serve again"}");
            return ExitCode.McpTokenUnavailable;
        }

        // The identity key is the trust anchor a client verifies before it hands over the token; only
        // serve mints it, in the same state directory, before the listener binds (ADR-0106 D1).
        var identityKeyFile = new IdentityKeyFile(cliInput.ServerConfig.Options);
        if (await identityKeyFile.EnsureAsync(ctx) is null)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: {identityKeyFile.RefusalReason ?? $"cannot read or create the identity key at {identityKeyFile.Path} — check its permissions, or remove it and start serve again"}");
            return ExitCode.McpTokenUnavailable;
        }

        if (tokenFile.TightenedStateDirectory || identityKeyFile.TightenedStateDirectory)
        {
            OwnerOnlyFile.Log.StateDirectoryTightened(logger, tokenFile.StateDirectory);
        }

        Log.McpTokenReady(logger, tokenFile.Path);

        return await StartHttpMcpServer(descriptor with { Token = mcpToken }, preBind.Believed, streams, ctx);
    }

    /// <summary>
    ///     The pre-bind state: an exit code to stop on, or what the probe and any restart established
    ///     about the port — which the bind is then judged against (ADR-0043).
    /// </summary>
    private async Task<PreBind> RestartServer(NodeLaunchDescriptor descriptor, StandardStreams streams, CancellationToken ctx)
    {
        var verdict = await serverProbe.ProbeAsync(descriptor.Port, ctx);
        if (RestartTransition.FromProbe(verdict) is { } settled)
        {
            if (settled is RestartOutcome.Unknown)
            {
                Log.ProbeUnanswered(logger, descriptor.Port);
            }

            return PreBind.Bind(settled);
        }

        if (!descriptor.Restarting)
        {
            // ADR-0106: an ai-raccoon server owns the port. A plain `serve` attaches to it only
            // after the proof; one that cannot prove is refused with the manual-stop remedy.
            return PreBind.ExitWith(await AttachOrRefuseAsync(descriptor, streams, ctx));
        }

        var restartResult = await serverRestart.CycleAsync(descriptor.Port, descriptor.TokenFile, ctx);
        if (RestartTransition.MayBind(restartResult.Outcome))
        {
            return PreBind.Bind(restartResult.Outcome);
        }

        var refusal = RestartRefusal(descriptor, restartResult);
        await streams.WriteErrorLineAsync(refusal.Message);
        return PreBind.ExitWith(refusal.Code);
    }


    private async Task<int> StartHttpMcpServer(NodeLaunchDescriptor descriptor, RestartOutcome believed, StandardStreams streams, CancellationToken ctx)
    {
        var serverHost = McpServerSetup.CreateWebHost(descriptor.ToServerConfig());
        try
        {
            var probeResolvingEncryptionKey = await encryptionKeyResolver.ProbeResolvingEncryptionKeyAsync(ctx);
            if (!probeResolvingEncryptionKey.IsSuccess)
            {
                return ExitCode.FailedToResolveEncryptionKey;
            }

            var probeUsingEncryptionKey = await connectionFactory.ProbeUsingEncryptionKey(probeResolvingEncryptionKey.Key.Passphrase, ctx);
            if (!probeUsingEncryptionKey.IsCorrectKey)
            {
                return ExitCode.FailedToOpenEncryptedBank;
            }

            await embeddingAvailability.EnsureEmbeddingAvailabilityAsync(ctx);

            // D3: vec0 must match the configured engine's dimension before the first tool call —
            // a serverless `model embedding set` (no server around to drain it) leaves vec0 stale otherwise.
            // Server-only by construction (`cli-asks-the-server-acts`): NodeRunner is the one path
            // that becomes the server, never a CLI verb.
            await using (var connection = await connectionFactory.OpenBankAsync(ctx).ConfigureAwait(false))
            {
                await entryEmbedder.ReconcileVecDimensionsAsync(connection, ctx).ConfigureAwait(false);
                await codeEmbedder.ReconcileVecCodeDimensionsAsync(connection, ctx).ConfigureAwait(false);
            }

            await serverHost.StartAsync(ctx);
            await EmitBoundUrl(descriptor, streams, serverHost);
            await serverHost.WaitForShutdownAsync(ctx);
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            return await ReportBindRefusedAsync(descriptor, believed, await serverProbe.ProbeAsync(descriptor.Port, ctx), streams, ctx);
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

    /// <summary>
    ///     serve always uses http: an explicit --transport names the proxy entry point, not this
    ///     host, so it is ignored with a warning rather than silently (observability, not control).
    /// </summary>
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
    ///     What a refused bind is reported as. The bind failure is proof the port is held, so a
    ///     pre-check that believed otherwise is refuted here rather than repeated (ADR-0043).
    /// </summary>
    private async Task<int> ReportBindRefusedAsync(NodeLaunchDescriptor descriptor, RestartOutcome believed, ProbeVerdict afterBind, StandardStreams streams, CancellationToken ctx)
    {
        var port = descriptor.Port;
        switch (RestartTransition.AfterBindRefused(believed, afterBind, descriptor.Restarting))
        {
            case BindRefusal.Attach:
                return await AttachOrRefuseAsync(descriptor, streams, ctx);
            case BindRefusal.LostThePort:
                Log.RestartLostThePort(logger, port);
                await streams.WriteErrorLineAsync(
                    $"ai-raccoon: restart on port {port} did not take — another server took the port while this one was starting; check it with 'ai-raccoon serve observability pid --port {port}'");
                return ExitCode.RestartLostThePort;
            case BindRefusal.HeldUnidentified:
                Log.RestartProbeUnanswered(logger, port);
                await streams.WriteErrorLineAsync(
                    $"ai-raccoon: cannot restart the server on port {port}: it is in use but gave the probe no answer, so nothing was asked to stop — try again, stop the listener yourself, or serve on another port");
                return ExitCode.RestartProbeUnanswered;
            default:
                Log.PortInUse(logger, port);
                await streams.WriteErrorLineAsync($"ai-raccoon: port {port} is in use — pass --port 0 for a random port, or free the port");
                return ExitCode.PortInUse;
        }
    }

    /// <summary>
    ///     Proves the listener before a plain `serve` attaches to it (ADR-0106). A listener that
    ///     cannot prove it holds this root's identity key is refused: attaching would hand it the
    ///     token and tool traffic.
    /// </summary>
    private async Task<int> AttachOrRefuseAsync(NodeLaunchDescriptor descriptor, StandardStreams streams,
        CancellationToken ctx) =>
        await identityProver.ProveAsync(ServerProbe.EndpointFor(descriptor.Port), ctx) is null
            ? await ReportAttachedAsync(descriptor, streams)
            : await RefuseExistingServerAsync(descriptor, streams);

    /// <summary>
    ///     ADR-0106: an ai-raccoon server already owns the port but could not prove it serves this
    ///     data root. The operator is told to stop the listener (or pick another port) rather than
    ///     being handed a flag that no longer exists.
    /// </summary>
    private async Task<int> RefuseExistingServerAsync(NodeLaunchDescriptor descriptor, StandardStreams streams)
    {
        Log.PortInUse(logger, descriptor.Port);
        await streams.WriteErrorLineAsync(
            $"ai-raccoon: port {descriptor.Port} is in use by a listener that did not prove it serves this data root — stop the listener yourself, or serve on another port (--port 0)");
        return ExitCode.PortInUse;
    }

    /// <summary>
    ///     The operator line and exit code for a restart that cannot go ahead. Only outcomes
    ///     <see cref="RestartTransition.MayBind" /> rejects reach it.
    /// </summary>
    private static (string Message, int Code) RestartRefusal(NodeLaunchDescriptor descriptor, RestartResult result) =>
        result.Outcome switch
        {
            RestartOutcome.Foreign => (
                $"ai-raccoon: port {descriptor.Port} is held by a listener that does not identify as an ai-raccoon server — stop it yourself, or serve on another port",
                ExitCode.PortInUse),
            RestartOutcome.Unproven => (
                $"ai-raccoon: cannot restart the server on port {descriptor.Port}: the listener did not prove it serves this data root — stop the listener yourself, then run serve again, or serve on another port (--port 0)",
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
            RestartOutcome.TimedOut => (
                $"ai-raccoon: restart on port {descriptor.Port} timed out: the server (pid {result.Pid}) accepted the shutdown but still held the port {ServerRestart.PortFreeWithin.TotalSeconds:0}s later — stop it yourself, then run serve again",
                ExitCode.RestartTimedOut),
            _ => ThrowHelper.ThrowArgumentOutOfRangeException<(string, int)>(nameof(result),
                $"{result.Outcome} lets serve bind, so it has no refusal line")
        };

    private async Task<int> ReportAttachedAsync(NodeLaunchDescriptor descriptor, StandardStreams streams)
    {
        // UX-F10/ADR-0106: the proof binds the listener to this root's identity key, so the bank
        // is the one this invocation asked for. The line still names the root's bank path, and is
        // explicit that this process never opened it.
        var requestedBankPath = SqliteConnectionFactory.BankPathFor(descriptor.LaunchConfig.Options);
        Log.AttachedToExistingServer(logger, descriptor.Url, requestedBankPath);
        await streams.WriteErrorLineAsync(
            $"ai-raccoon: attached to the server already listening on {descriptor.Url} — it proved it holds the identity key for {requestedBankPath}'s state directory; this process never opened that bank. Stop that server to serve here, or use --port 0 for a private one");
        await streams.RenderUrlForInput(descriptor.Url, descriptor.Port, descriptor.Source.McpEntry, descriptor.Source.Format);
        return ExitCode.Success;
    }

    /// <summary>
    ///     Either an exit code to stop on, or what the pre-check established about the port —
    ///     <see cref="RestartOutcome.Unknown" /> whenever nothing was established at all.
    /// </summary>
    private sealed record PreBind(int? ExitNow, RestartOutcome Believed)
    {
        public static PreBind Bind(RestartOutcome believed) => new(null, believed);

        public static PreBind ExitWith(int code) => new(code, RestartOutcome.Unknown);
    }

    private sealed record NodeLaunchDescriptor(ServerConfig LaunchConfig)
    {
        public string Url => $"http://127.0.0.1:{Port}/mcp";
        public required NodeCliOptions Source { get; init; }
        public required int Port { get; init; }
        public required TimeSpan IdleTimeout { get; set; }
        public required bool Restarting { get; init; }
        public required McpTokenFile TokenFile { get; init; }
        public string Token { get; init; } = "";

        public ServerConfig ToServerConfig() =>
            new(Port, McpTransport.Http, LaunchConfig.Options, IdleTimeout)
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

        [LoggerMessage(EventId = 604, Level = LogLevel.Debug,
            Message = "ai-raccoon: port {Port} gave the probe no answer; whether anything holds it is unknown")]
        public static partial void ProbeUnanswered(ILogger logger, int port);

        [LoggerMessage(EventId = 609, Level = LogLevel.Error,
            Message = "ai-raccoon: port {Port} is in use but gave the probe no answer; nothing was asked to stop")]
        public static partial void RestartProbeUnanswered(ILogger logger, int port);

        [LoggerMessage(EventId = 605, Level = LogLevel.Information, Message = "ai-raccoon: attached to the server already listening on {Url} — it proved this root's identity key for {RequestedBankPath}")]
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
