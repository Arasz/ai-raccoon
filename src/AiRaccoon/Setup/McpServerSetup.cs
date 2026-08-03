using AiRaccoon.Prompts;
using AiRaccoon.Tools;

namespace AiRaccoon.Setup;

/// <summary>
///     Decides which MCP transport the server should use from the MCP_TRANSPORT
///     environment variable. Anything other than "http" (case-insensitive) runs stdio.
/// </summary>
internal static partial class McpServerSetup
{
    private static readonly IReadOnlyCollection<McpTransport> DefaultTransport = [McpTransport.Stdio];

    private static readonly Lazy<IReadOnlyCollection<McpTransport>> ConfiguredTransport = new(() =>
        SelectTransports(Environment.GetEnvironmentVariable("MCP_TRANSPORT")));

    /// <summary>
    ///     Resolves the MCP_TRANSPORT env value to the transports to enable; anything other than "http"
    ///     (case-insensitive) runs stdio.
    /// </summary>
    internal static IReadOnlyCollection<McpTransport> SelectTransports(string? transport) =>
        Enum.TryParse<McpTransport>(transport, true, out var mcpTransport)
            ? [mcpTransport]
            : DefaultTransport;

    extension(WebApplication webApplication)
    {
        public WebApplication ConfigureMcpEndpoints()
        {
            var transport = ConfiguredTransport.Value;
            if (transport.Contains(McpTransport.Https))
            {
                Log.HttpsTransportNotSupported(webApplication.Logger);
            }

            if (transport.Contains(McpTransport.Http))
            {
                webApplication.MapMcp("/mcp");
            }

            return webApplication;
        }
    }

    extension(WebApplicationBuilder webApplicationBuilder)
    {
        public WebApplicationBuilder ConfigureMcpServer()
        {
            var transport = ConfiguredTransport.Value;

            webApplicationBuilder
                .Services
                .AddMcpServer()
                .ConfigureMcpTransport(transport, webApplicationBuilder.Logging)
                .WithTools<MemoryTools>()
                .WithPrompts<MemoryPrompts>();

            return webApplicationBuilder;
        }
    }

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
            loggingBuilder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
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
