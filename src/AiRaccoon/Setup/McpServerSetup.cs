using System.Net;
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
    ///     Creates the server host for transport set: stdio-only launches on a plain app
    ///     host with no web server; any HTTP/S presence uses a web host bound to the
    ///     configured port (never the ASP.NET default 5000), with stdio attached when both
    ///     are selected. The optional timeProvider is a test seam for the fake-clock
    ///     watchdog tests; null keeps TimeProvider.System.
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
            .WithTools<WatchTools>()
            .WithPrompts<MemoryPrompts>();
        return builder.Build();
    }

    private static IHost CreateWebHost(ServerConfig config, IReadOnlyCollection<McpTransport> transports,
        TimeProvider? timeProvider = null)
    {
        var builder = WebApplication.CreateBuilder([]); // args already consumed by CliArgs
        builder.Configuration.Sources.Clear(); // Ruling 3: the settings table is the only runtime channel
        builder.Services.RegisterMemoryServices(config.Options, transports);
        builder.Services.AddSingleton(timeProvider ?? TimeProvider.System); // test seam: fake clock for the watchdog
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        if (config.Options.Quiet)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, config.Port));
        if (config.IdleTimeout > TimeSpan.Zero)
        {
            // R1: three registrations, one instance — AddHostedService<T> registers only
            // IHostedService→T, so the middleware's GetRequiredService<IdleWatchdog>()
            // would throw on the first request without the singleton registrations.
            // The TimeSpan registration feeds the type-based AddSingleton<IdleWatchdog>().
            builder.Services.AddSingleton(typeof(TimeSpan), config.IdleTimeout);
            builder.Services.AddSingleton<IdleWatchdog>();
            builder.Services.AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());
        }

        builder.ConfigureMcpServer(transports);
        return builder.Build().ConfigureMcpEndpoints(transports, armWatchdog: config.IdleTimeout > TimeSpan.Zero);
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
                // Spliced between routing and endpoints (.NET 10): wraps /mcp and
                // path-checks inside, so 404s on other paths never signal (R4).
                webApplication.UseMiddleware<McpActivityMiddleware>();
            }

            if (transports.Contains(McpTransport.Http))
            {
                webApplication.MapMcp("/mcp");
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
                .ConfigureMcpTransport(transports, webApplicationBuilder.Logging, quietInfo: quietInfo)
                .WithTools<MemoryTools>()
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
            AddStderrConsoleLogging(loggingBuilder, quietInfo: quietInfo);
            return mcpServerBuilder.WithStdioServerTransport();
        }

        private IMcpServerBuilder HandleHttpTransport() =>
            mcpServerBuilder.WithHttpTransport(options =>
            {
                // Stateless mode is recommended for servers that don't need
                // server-to-client requests like sampling or elicitation.
                options.Stateless = true;
            });

        private IMcpServerBuilder HandleHttpsTransport() =>
            // Unsupported: no transport is configured for https; the warning is emitted
            // once in ConfigureMcpEndpoints where the app logger is available.
            mcpServerBuilder;
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "ai-raccoon: https transport is not supported")]
        public static partial void HttpsTransportNotSupported(ILogger logger);
    }
}

public enum McpTransport
{
    Stdio = 0,
    Http = 1,
    Https = 2
}
