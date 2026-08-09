using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
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
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly HttpClient ProbeClient = new() { Timeout = ProbeTimeout };

    public static async Task<int> RunAsync(CliParseResult parsed, ServerConfig config, TextWriter stdout,
        TextWriter stderr, CancellationToken cancellationToken = default)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));
        var logger = loggerFactory.CreateLogger("ServeRunner");

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
        if (await TryProbeAttachAsync(port, cancellationToken))
        {
            return await ReportAttachedAsync(url, parsed, stdout, stderr, logger);
        }

        var app = McpServerSetup.CreateServerHost(serveConfig);
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
            if (await TryProbeAttachAsync(port, cancellationToken))
            {
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

    /// <summary>R14 probe (docs/plans/2026-08-06-http-serve-mode-plan.md): POST /mcp with an MCP
    /// Accept header and a non-JSON body; recognized iff status ∈ {400,405,406} and the body
    /// mentions jsonrpc. 2 attempts, 1s timeout each.</summary>
    private static async Task<bool> TryProbeAttachAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp")
                {
                    // JSON content type with a non-JSON body: the MCP endpoint answers 400
                    // with a JSON-RPC error body (415 only when the content type is wrong).
                    Content = new StringContent("x", Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
                };
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                using var response = await ProbeClient.SendAsync(request, cancellationToken);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotAcceptable)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (body.Contains("jsonrpc", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Connection refused or probe timeout: not an ai-raccoon server.
            }
        }

        return false;
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
    }
}
