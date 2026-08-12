namespace AiRaccoon.Setup.Cli.Render;

public static class StreamRenderingExtensions
{
    public static async Task RenderUrlForInput(this StandardStreams streams, string url, int port, bool isMcpEntry, string format)
    {
        if (isMcpEntry)
        {
            await streams.WriteOutputLineAsync(format switch
            {
                "claude" => McpEntryRenderer.RenderClaude(port),
                "all" => McpEntryRenderer.RenderAll(port),
                _ => McpEntryRenderer.RenderHermes(port)
            });
        }
        else
        {
            await streams.WriteOutputLineAsync(url);
        }
    }
}
