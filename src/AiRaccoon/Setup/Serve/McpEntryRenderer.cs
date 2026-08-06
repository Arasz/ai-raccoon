namespace AiRaccoon.Setup.Serve;

/// <summary>Renders the serve --mcp-entry client-config documents: single-line JSON, loopback host (127.0.0.1), /mcp path.</summary>
public static class McpEntryRenderer
{
    public static string RenderHermes(int port) => $$$$"""{"ai-raccoon":{"url":"http://127.0.0.1:{{{{port}}}}/mcp"}}""";

    public static string RenderClaude(int port) => $$$$"""{"mcpServers":{"ai-raccoon":{"type":"http","url":"http://127.0.0.1:{{{{port}}}}/mcp"}}}""";

    public static string RenderAll(int port) => $"{RenderHermes(port)}\n{RenderClaude(port)}";
}
