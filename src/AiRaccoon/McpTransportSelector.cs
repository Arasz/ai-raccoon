namespace AiRaccoon;

/// <summary>
/// Decides which MCP transport the server should use from the MCP_TRANSPORT
/// environment variable. Anything other than "http" (case-insensitive) runs stdio.
/// </summary>
internal static class McpTransportSelector
{
    public static bool UseHttp(string? transport)
    {
        return string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase);
    }

    extension(IMcpServerBuilder mcpServerBuilder)
    {
        public IMcpServerBuilder ConfigureMcpTransport(IReadOnlyCollection<McpTransport> selectedTransports)
        {
            if (selectedTransports.Count == 0)
            {
                return mcpServerBuilder.HandleStdioTransport();
            }

            foreach (var selectedTransport in selectedTransports)
            {
                mcpServerBuilder = selectedTransport switch
                {
                    McpTransport.Stdio => mcpServerBuilder.HandleStdioTransport(),
                    McpTransport.Http => mcpServerBuilder.HandleHttpTransport(),
                    McpTransport.Https => mcpServerBuilder.HandleHttpsTransport(),
                    _ => mcpServerBuilder
                };
            }

            return mcpServerBuilder;
        }

        private IMcpServerBuilder HandleStdioTransport()
        {
            return mcpServerBuilder.WithStdioServerTransport();
        }

        private IMcpServerBuilder HandleHttpTransport()
        {
            return mcpServerBuilder.WithHttpTransport(options =>
            {
                // Stateless mode is recommended for servers that don't need
                // server-to-client requests like sampling or elicitation.
                options.Stateless = true;
            });
        }

        private IMcpServerBuilder HandleHttpsTransport()
        {
            Console.Error.WriteLine("ai-raccoon: https transport is not supported");
            return mcpServerBuilder;
        }
    }
}

public enum McpTransport
{
    Stdio = 0,
    Http = 1,
    Https = 2
}
