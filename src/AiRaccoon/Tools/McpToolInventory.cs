namespace AiRaccoon.Tools;

/// <summary>
///     The MCP tool surface generated from this assembly's `[McpServerTool]` methods. Infrastructure
///     cannot see these types, so `memory_performance` passes the inventory in as data.
/// </summary>
public static class McpToolInventory
{
    public static IReadOnlyList<string> Names() => McpToolInventoryGenerated.Names;
}
