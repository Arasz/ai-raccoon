using System.Net;
using AiRaccoon.Prompts;
using AiRaccoon.Tools;

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
    ///     are selected.
    /// </summary>
    internal static IHost CreateServerHost(ServerConfig config, IReadOnlyCollection<McpTransport> transports)
    {
        if (transports.Count == 1 && transports.Contains(McpTransport.Stdio))
        {
            return CreateAppHost(config);
        }

        return CreateWebHost(config, transports);
    }

    private static IHost CreateAppHost(ServerConfig config)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear(); // Ruling 3: the settings table is the only runtime channel
        builder.Services.RegisterMemoryServices(config.Options, registerExtractionHostedService: false);
        builder.Services
            .AddMcpServer()
            .ConfigureMcpTransport([McpTransport.Stdio], builder.Logging)
            .WithTools<MemoryTools>()
            .WithTools<WatchTools>()
            .WithPrompts<MemoryPrompts>();
        return builder.Build();
    }

    private static IHost CreateWebHost(ServerConfig config, IReadOnlyCollection<McpTransport> transports)
    {
        var builder = WebApplication.CreateBuilder([]); // args already consumed by CliArgs
        builder.Configuration.Sources.Clear(); // Ruling 3: the settings table is the only runtime channel
        builder.Services.RegisterMemoryServices(config.Options);
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        // Explicit endpoint: bind the configured port (7721 default, 0 = random) instead
        // of the ASP.NET default 5000, which collides with other listeners on the host.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, config.Port));
        builder.ConfigureMcpServer(transports);
        return builder.Build().ConfigureMcpEndpoints(transports);
    }

    extension(WebApplication webApplication)
    {
        private WebApplication ConfigureMcpEndpoints(IReadOnlyCollection<McpTransport> transports)
        {
            if (transports.Contains(McpTransport.Https))
            {
                Log.HttpsTransportNotSupported(webApplication.Logger);
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
        private void ConfigureMcpServer(IReadOnlyCollection<McpTransport> transports) =>
            webApplicationBuilder
                .Services
                .AddMcpServer()
                .ConfigureMcpTransport(transports, webApplicationBuilder.Logging)
                .WithTools<MemoryTools>()
                .WithTools<WatchTools>()
                .WithPrompts<MemoryPrompts>();
    }

    private static void AddStderrConsoleLogging(ILoggingBuilder loggingBuilder) => loggingBuilder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    extension(IMcpServerBuilder mcpServerBuilder)
    {
        private IMcpServerBuilder ConfigureMcpTransport(IReadOnlyCollection<McpTransport> selectedTransports,
            ILoggingBuilder loggingBuilder)
        {
            if (selectedTransports.Count == 0)
            {
                return mcpServerBuilder.HandleStdioTransport(loggingBuilder);
            }

            foreach (var selectedTransport in selectedTransports)
            {
                mcpServerBuilder = selectedTransport switch
                {
                    McpTransport.Stdio => mcpServerBuilder.HandleStdioTransport(loggingBuilder),
                    McpTransport.Http => mcpServerBuilder.HandleHttpTransport(),
                    McpTransport.Https => mcpServerBuilder.HandleHttpsTransport(),
                    _ => mcpServerBuilder
                };
            }

            return mcpServerBuilder;
        }

        private IMcpServerBuilder HandleStdioTransport(ILoggingBuilder loggingBuilder)
        {
            AddStderrConsoleLogging(loggingBuilder);
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

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "ai-raccoon: http transport listening on {Urls}")]
        public static partial void HttpTransportListening(ILogger logger, string urls);
    }
}

public enum McpTransport
{
    Stdio = 0,
    Http = 1,
    Https = 2
}
