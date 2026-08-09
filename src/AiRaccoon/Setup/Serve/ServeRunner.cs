using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Sockets;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Setup.Cli;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     Serve-mode composition root (Program.cs): force http, probe-attach to an existing
///     ai-raccoon server on the port (docs/plans/2026-08-06-http-serve-mode-plan.md R14), else
///     bootstrap like the bare launch path, report the bound URL on stdout, and log to stderr.
/// </summary>
internal static partial class ServeRunner
{
    private static readonly ServerProbe Probe = ServerProbe.ForLoopback();

    public static async Task<int> RunAsync(CliParseResult parsed, ServerConfig config, TextWriter stdout,
        TextWriter stderr, CancellationToken cancellationToken = default)
    {
        using var log = PreHostLogging.CreateLogger("ServeRunner", config.Options);
        var logger = log.Logger;

        var port = ResolvePort(parsed);
        var serveConfig = config with
        {
            Port = port,
            Transport = McpTransport.Http,
            IdleTimeout = ResolveIdleTimeout(parsed)
        };
        WarnOnNonHttpTransport(parsed, stderr, logger);

        // Probe first, before bank/key/embedding work (docs/plans/2026-08-06-http-serve-mode-plan.md R14):
        // attach mode never arms the watchdog, touches the bank, or honors --idle-timeout.
        var url = $"http://127.0.0.1:{port}/mcp";
        var restarting = parsed.ParseResult.GetValue(CliCommandTree.ServeRestartOption);
        var tokenFile = new McpTokenFile(config.Options.DataRoot);
        if (await Probe.RespondsAsync(port, cancellationToken))
        {
            if (!restarting)
            {
                return await ReportAttachedAsync(url, parsed, stdout, stderr, logger);
            }

            var restart = await new ServerRestart(Probe, logger).CycleAsync(port, tokenFile, cancellationToken);
            if (RestartRefusal(restart, port, tokenFile.Path) is { } refusal)
            {
                await stderr.WriteLineAsync(refusal.Message);
                return refusal.Code;
            }
        }

        // Minted strictly before the Kestrel bind: once the port answers, the token file is on
        // disk, so the proxy never has to poll for it (ADR-0020).
        if (await tokenFile.EnsureAsync(cancellationToken) is not { } mcpToken)
        {
            Log.McpTokenUnavailable(logger, tokenFile.Path);
            await stderr.WriteLineAsync(
                $"ai-raccoon: cannot read or create the MCP token at {tokenFile.Path} — check its permissions, or remove it and start serve again");
            return ExitCode.McpTokenUnavailable;
        }

        Log.McpTokenReady(logger, tokenFile.Path);
        var app = McpServerSetup.CreateServerHost(serveConfig with { McpToken = mcpToken });
        try
        {
            if (!TryResolveEncryptionKey(logger, app.Services.GetRequiredService<IEncryptionKeyResolver>(), out var encryptionKey))
            {
                return ExitCode.FailedToResolveEncryptionKey;
            }

            if (!await TryProbeBankDecryption(logger, app.Services.GetRequiredService<SqliteConnectionFactory>(), encryptionKey, cancellationToken))
            {
                return ExitCode.FailedToOpenEncryptedBank;
            }

            await app.Services.GetRequiredService<EmbeddingAvailability>().EnsureEmbeddingAvailabilityAsync(cancellationToken);

            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            // Concurrent-start bind race: re-probe once, then attach or fail with the
            // actionable PortInUse line. Never auto-fallback to a random port.
            if (await Probe.RespondsAsync(port, cancellationToken))
            {
                if (restarting)
                {
                    // A restart that attached would report success for the server it was asked
                    // to replace: another start won the port, and that has to be said out loud.
                    Log.RestartLostThePort(logger, port);
                    await stderr.WriteLineAsync(
                        $"ai-raccoon: restart on port {port} did not take — another server took the port while this one was starting; check it with 'ai-raccoon serve observability pid --port {port}'");
                    return ExitCode.RestartFailed;
                }

                return await ReportAttachedAsync(url, parsed, stdout, stderr, logger);
            }

            Log.PortInUse(logger, port);
            await stderr.WriteLineAsync($"ai-raccoon: port {port} is in use — pass --port 0 for a random port, or free the port");
            return ExitCode.PortInUse;
        }

        // The bound URL is only knowable after StartAsync (port 0 = random); web.Urls is
        // the pattern HostExtensions.RunAsync uses.
        var boundUrl = $"{((WebApplication)app).Urls.First().TrimEnd('/')}/mcp";
        Log.ServeListening(logger, boundUrl);
        await WriteOutputLineAsync(boundUrl, parsed, stdout);

        await app.WaitForShutdownAsync(cancellationToken);
        if (app is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }

        return ExitCode.Success;
    }

    /// <summary>Serve's own --port wins; else the root --port; else 7721. Reads are instance-based
    /// — name-based GetResult("--port") would resolve the root option
    /// (docs/plans/2026-08-06-http-serve-mode-plan.md R7/R12).</summary>
    private static int ResolvePort(CliParseResult parsed)
    {
        if (parsed.ParseResult.GetResult(CliCommandTree.ServePortOption) is OptionResult { Tokens.Count: > 0 })
        {
            return parsed.ParseResult.GetValue(CliCommandTree.ServePortOption);
        }

        if (parsed.ParseResult.GetResult(CliCommandTree.LaunchPortOption) is OptionResult { Tokens.Count: > 0 })
        {
            return parsed.ParseResult.GetValue(CliCommandTree.LaunchPortOption);
        }

        return DefaultOptions.Port;
    }

    /// <summary>Serve applies the 4h default when --idle-timeout is absent; 0 disables the watchdog.</summary>
    private static TimeSpan ResolveIdleTimeout(CliParseResult parsed)
    {
        if (parsed.ParseResult.GetResult(CliCommandTree.ServeIdleTimeoutOption) is OptionResult { Tokens.Count: > 0 })
        {
            var value = parsed.ParseResult.GetValue(CliCommandTree.ServeIdleTimeoutOption);
            return IdleTimeoutParser.TryParse(value, out var timeout) ? timeout : DefaultOptions.IdleTimeout;
        }

        return DefaultOptions.IdleTimeout;
    }

    private static void WarnOnNonHttpTransport(CliParseResult parsed, TextWriter stderr, ILogger logger)
    {
        if (parsed.ParseResult.GetResult("--transport") is not OptionResult { Tokens.Count: > 0 } transport)
        {
            return;
        }

        var selected = transport.GetValueOrDefault<McpTransport>();
        if (selected == McpTransport.Http)
        {
            return;
        }

        Log.IgnoringTransport(logger, selected);
        stderr.WriteLine($"ai-raccoon: serve ignoring --transport {selected}; serve always uses http");
    }

    /// <summary>True when the exception chain marks the bind failure as address-in-use
    /// (Kestrel surfaces Microsoft.AspNetCore.Connections.AddressInUseException or an
    /// IOException wrapping a SocketException with AddressAlreadyInUse).</summary>
    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException)
            {
                return true;
            }

            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The operator line and exit code for a restart that cannot go ahead, or null when
    /// `serve` may bind: nothing was listening, or the server stopped and freed the port.</summary>
    private static (string Message, int Code)? RestartRefusal(RestartResult restart, int port, string tokenPath) =>
        restart.Outcome switch
        {
            RestartOutcome.Nothing or RestartOutcome.Stopped => null,
            RestartOutcome.Foreign => (
                $"ai-raccoon: port {port} is held by a listener that does not identify as an ai-raccoon server — stop it yourself, or serve on another port",
                ExitCode.PortInUse),
            RestartOutcome.NoToken => (
                $"ai-raccoon: cannot restart the server on port {port}: {tokenPath} holds no token, so it cannot be asked to stop — it may serve another data root; stop it yourself, or serve on another port",
                ExitCode.RestartFailed),
            RestartOutcome.Refused => (
                $"ai-raccoon: cannot restart the server on port {port}: it refused the token in {tokenPath} — it serves another data root; stop it yourself, or serve on another port",
                ExitCode.RestartFailed),
            RestartOutcome.Unsupported => (
                $"ai-raccoon: cannot restart the server on port {port}: the ai-raccoon {restart.Version ?? ServerRestart.UnknownVersion} serving it (pid {restart.Pid}) is too old to be asked to stop — stop it yourself, then run serve again",
                ExitCode.RestartFailed),
            _ => (
                $"ai-raccoon: restart on port {port} timed out: the server (pid {restart.Pid}) accepted the shutdown but still held the port {ServerRestart.PortFreeWithin.TotalSeconds:0}s later — stop it yourself, then run serve again",
                ExitCode.RestartFailed)
        };

    private static async Task<int> ReportAttachedAsync(string url, CliParseResult parsed, TextWriter stdout,
        TextWriter stderr, ILogger logger)
    {
        Log.AttachedToExistingServer(logger, url);
        await stderr.WriteLineAsync($"ai-raccoon: attached to the server already listening on {url}");
        await WriteOutputLineAsync(url, parsed, stdout);
        return ExitCode.Success;
    }

    private static async Task WriteOutputLineAsync(string url, CliParseResult parsed, TextWriter stdout)
    {
        if (parsed.ParseResult.GetValue(CliCommandTree.ServeMcpEntryOption))
        {
            var port = new Uri(url).Port;
            var format = parsed.ParseResult.GetValue(CliCommandTree.ServeFormatOption);
            await stdout.WriteLineAsync(format switch
            {
                "claude" => McpEntryRenderer.RenderClaude(port),
                "all" => McpEntryRenderer.RenderAll(port),
                _ => McpEntryRenderer.RenderHermes(port)
            });
        }
        else
        {
            await stdout.WriteLineAsync(url);
        }
    }

    private static bool TryResolveEncryptionKey(ILogger logger, IEncryptionKeyResolver encryptionKeyResolver, out ResolvedKey resolvedKey)
    {
        try
        {
            resolvedKey = encryptionKeyResolver.Resolve();
            return true;
        }
        catch (Exception ex)
        {
            AiRaccoon.Log.FailedToResolveEncryptionKey(logger, ex);
            resolvedKey = ResolvedKey.None;
            return false;
        }
    }

    private static async Task<bool> TryProbeBankDecryption(ILogger logger, SqliteConnectionFactory sqliteConnectionFactory,
        ResolvedKey resolvedKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var probe = await sqliteConnectionFactory.OpenBankWithKeyAsync(resolvedKey.Passphrase, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            AiRaccoon.Log.FailedToOpenEncryptedBank(logger, resolvedKey.SourceName, ex.Message, ex);
            return false;
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 601, Level = LogLevel.Debug, Message = "ai-raccoon: serve listening on {Url}")]
        public static partial void ServeListening(ILogger logger, string url);

        [LoggerMessage(EventId = 602, Level = LogLevel.Warning, Message = "ai-raccoon: serve ignoring --transport {Transport}; serve always uses http")]
        public static partial void IgnoringTransport(ILogger logger, McpTransport transport);

        [LoggerMessage(EventId = 603, Level = LogLevel.Error, Message = "ai-raccoon: port {Port} is in use — pass --port 0 for a random port, or free the port")]
        public static partial void PortInUse(ILogger logger, int port);

        [LoggerMessage(EventId = 605, Level = LogLevel.Information, Message = "ai-raccoon: attached to the server already listening on {Url}")]
        public static partial void AttachedToExistingServer(ILogger logger, string url);

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
