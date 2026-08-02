/// <summary>
/// Decides which MCP transport the server should use from the MCP_TRANSPORT
/// environment variable. Anything other than "http" (case-insensitive) runs stdio.
/// </summary>
internal static class McpTransportSelector
{
    public static bool UseHttp(string? transport) =>
        string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase);
}
