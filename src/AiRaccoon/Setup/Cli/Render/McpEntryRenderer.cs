using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Setup.Cli.Render;

/// <summary>
///     Renders the serve --mcp-entry client-config documents: single-line JSON, loopback host
///     (127.0.0.1), /mcp path, and the token header as a `${VAR}` placeholder — never a live
///     secret (docs/plans/2026-08-09-http-token-clients.md D4).
/// </summary>
public static class McpEntryRenderer
{
    public static string RenderHermes(int port) => $$$$"""{"ai-raccoon":{"url":"http://127.0.0.1:{{{{port}}}}/mcp","headers":{"{{{{McpTokenGate.HeaderName}}}}":"${AIRACCOON_MCP_TOKEN}"}}}""";

    public static string RenderClaude(int port) =>
        $$$$$"""{"mcpServers":{"ai-raccoon":{"type":"http","url":"http://127.0.0.1:{{{{{port}}}}}/mcp","headers":{"{{{{{McpTokenGate.HeaderName}}}}}":"${AIRACCOON_MCP_TOKEN}"}}}}""";

    public static string RenderAll(int port) => $"{RenderHermes(port)}\n{RenderClaude(port)}";
}
