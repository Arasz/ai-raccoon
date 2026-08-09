using System.Collections;
using System.Net;
using AiRaccoon.Observability;
using AiRaccoon.Prompts;
using AiRaccoon.Setup.Serve;
using AiRaccoon.Tools;
using DotNext.Collections.Generic;

namespace AiRaccoon.Setup;

/// <summary>
///     Wires the MCP server for resolved transport set: a plain app host for stdio-only
///     (no web server, no HTTP bind), a web host for HTTP/S, and a web host with stdio
///     attached for a combined set. https is declared but unsupported (warning only).
/// </summary>
internal static partial class McpServerSetup
{
    private static readonly IReadOnlyCollection<McpTransport> DefaultTransport = [McpTransport.Stdio];

    /// <summary>
    ///     Resolves the --transport value to the transports to enable; anything other than
    ///     "http" (case-insensitive) runs stdio.
    /// </summary>
    internal static IReadOnlyCollection<McpTransport> SelectTransports(string? transport) =>
        Enum.TryParse<McpTransport>(transport, true, out var mcpTransport)
            ? [mcpTransport]
            : DefaultTransport;

    /// <summary>Creates the server host for the config's single transport.</summary>
    internal static IHost CreateServerHost(ServerConfig config) => CreateServerHost(config, [config.Transport]);

    /// <summary>
    ///     Creates the server host for transport set: stdio-only launches a plain app host; any
    ///     HTTP/S presence uses a web host on the configured port, with stdio attached too when
    ///     both are selected. timeProvider is a test seam for the watchdog tests; null keeps TimeProvider.System.
    /// </summary>
    internal static IHost CreateServerHost(ServerConfig config, IReadOnlyCollection<McpTransport> transports,
        TimeProvider? timeProvider = null)
    {
        if (transports.Count == 1 && transports.Contains(McpTransport.Stdio))
        {
            return CreateAppHost(config);
        }

        return CreateWebHost(config, transports, timeProvider);
    }

    private static IHost CreateAppHost(ServerConfig config)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        var mcpTransport = IReadOnlyList<McpTransport>.Singleton(McpTransport.Stdio);
        builder.Services.RegisterMemoryServices(config.Options, mcpTransport);
        builder.Services
            .AddMcpServer()
            .ConfigureMcpTransport(mcpTransport, builder.Logging, config.Options.Quiet)
            .WithTools<MemoryTools>()
            .WithTools<ShareTools>()
            .WithTools<WorkspaceTools>()
            .WithTools<SweepTools>()
            .WithTools<SyncTools>()
            .WithTools<PromotionTools>()
            .WithTools<WatchTools>()
            .WithPrompts<MemoryPrompts>();
        return builder.Build();
    }

    private static IHost CreateWebHost(ServerConfig config, IReadOnlyCollection<McpTransport> transports,
        TimeProvider? timeProvider = null)
    {
        var builder = WebApplication.CreateBuilder([]); // args already consumed by CliArgs
        builder.Configuration.Sources.Clear(); // Ruling 3: the settings table is the only runtime channel
        // Re-admits OTEL_*-prefixed env vars only (ADR-0009): the OTel SDK reads its entire OTEL_*
        // surface through IConfiguration, not hand-rolled reads; this keeps Ruling 3 intact otherwise.
        builder.Configuration.AddInMemoryCollection(
            Environment.GetEnvironmentVariables()
                .Cast<DictionaryEntry>()
                .Where(e => e.Key.ToString()!.StartsWith("OTEL_", StringComparison.Ordinal))
                .ToDictionary(e => e.Key.ToString()!, e => (string?)e.Value?.ToString()));
        builder.Services.RegisterMemoryServices(config.Options, transports);
        // Web host only (docs/adr/0009-otlp-export.md): stdio hosts recycle too often to pay
        // the exporter's batch delay / provider shutdown grace.
        builder.Services.AddOtlpExport();
        builder.Services.AddSingleton(timeProvider ?? TimeProvider.System); // test seam: fake clock for the watchdog
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        // Per-request ASP.NET Core / MCP server INFO chatter carries nothing an operator acts on
        // above Warning, so both categories are floored here regardless of --quiet.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
        if (config.Options.Quiet)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, config.Port));
        if (config.IdleTimeout > TimeSpan.Zero)
        {
            // Three registrations, one instance (docs/plans/2026-08-06-http-serve-mode-plan.md R1):
            // AddHostedService<T> only registers IHostedService→T, so the middleware's DI lookup would fail otherwise.
            builder.Services.AddSingleton(typeof(TimeSpan), config.IdleTimeout);
            builder.Services.AddSingleton<IdleWatchdog>();
            builder.Services.AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());
        }

        builder.ConfigureMcpServer(transports);
        return builder.Build().ConfigureMcpEndpoints(transports, config.IdleTimeout > TimeSpan.Zero);
    }

    extension(WebApplication webApplication)
    {
        private WebApplication ConfigureMcpEndpoints(IReadOnlyCollection<McpTransport> transports, bool armWatchdog)
        {
            if (transports.Contains(McpTransport.Https))
            {
                Log.HttpsTransportNotSupported(webApplication.Logger);
            }

            if (armWatchdog)
            {
                // Spliced between routing and endpoints (.NET 10): branches on /mcp so 404s on
                // other paths never signal (docs/plans/2026-08-06-http-serve-mode-plan.md R4).
                webApplication.UseMiddleware<McpActivityMiddleware>();
            }

            if (transports.Contains(McpTransport.Http))
            {
                webApplication.MapMcp("/mcp");
                webApplication.MapObservability();
            }

            return webApplication;
        }
    }

    extension(WebApplicationBuilder webApplicationBuilder)
    {
        private void ConfigureMcpServer(IReadOnlyCollection<McpTransport> transports, bool quietInfo = false) =>
            webApplicationBuilder
                .Services
                .AddMcpServer()
                .ConfigureMcpTransport(transports, webApplicationBuilder.Logging, quietInfo)
                .WithTools<MemoryTools>()
                .WithTools<ShareTools>()
                .WithTools<WorkspaceTools>()
                .WithTools<SweepTools>()
                .WithTools<SyncTools>()
                .WithTools<PromotionTools>()
                .WithTools<WatchTools>()
                .WithPrompts<MemoryPrompts>();
    }

    private static void AddStderrConsoleLogging(ILoggingBuilder loggingBuilder, bool quietInfo = false)
    {
        loggingBuilder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        if (quietInfo)
        {
            // Quiet mode: the caller (e.g. the Hermes provider) emits its own status cues;
            // warnings and errors still surface.
            loggingBuilder.SetMinimumLevel(LogLevel.Warning);
        }
    }

    extension(IMcpServerBuilder mcpServerBuilder)
    {
        private IMcpServerBuilder ConfigureMcpTransport(IReadOnlyCollection<McpTransport> selectedTransports,
            ILoggingBuilder loggingBuilder, bool quietInfo = false)
        {
            mcpServerBuilder = mcpServerBuilder.WithRequestFilters(f => f.AddCallToolFilter(ToolRefusals.Filter));

            if (selectedTransports.Count == 0)
            {
                return mcpServerBuilder.HandleStdioTransport(loggingBuilder, quietInfo);
            }

            foreach (var selectedTransport in selectedTransports)
            {
                mcpServerBuilder = selectedTransport switch
                {
                    McpTransport.Stdio => mcpServerBuilder.HandleStdioTransport(loggingBuilder, quietInfo),
                    McpTransport.Http => mcpServerBuilder.HandleHttpTransport(),
                    McpTransport.Https => mcpServerBuilder.HandleHttpsTransport(),
                    _ => mcpServerBuilder
                };
            }

            return mcpServerBuilder;
        }

        private IMcpServerBuilder HandleStdioTransport(ILoggingBuilder loggingBuilder, bool quietInfo = false)
        {
            AddStderrConsoleLogging(loggingBuilder, quietInfo);
            return mcpServerBuilder.WithStdioServerTransport();
        }

        private IMcpServerBuilder HandleHttpTransport() => mcpServerBuilder.WithHttpTransport(options => { options.Stateless = true; });

        private IMcpServerBuilder HandleHttpsTransport() => mcpServerBuilder;
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "ai-raccoon: https transport is not supported")]
        public static partial void HttpsTransportNotSupported(ILogger logger);
    }
}

public enum McpTransport
{
    Stdio = 0,
    Http = 1,
    Https = 2,

    /// <summary>A stdio front end that relays every message to one HTTP backend (ADR-0020).</summary>
    Proxy = 3
}
