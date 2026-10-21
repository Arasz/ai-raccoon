namespace AiRaccoon.Tools;

/// <summary>The MCP tool names and families that require an ordinary project.</summary>
public sealed class ToolNameScope : IToolNameScope
{
    /// <inheritdoc />
    public bool RequiresOrdinaryProject(string toolName) =>
        toolName.StartsWith("memory_workspace_", StringComparison.Ordinal)
        || toolName.StartsWith("memory_promotion_", StringComparison.Ordinal)
        || toolName is "memory_write" or "memory_share" or "memory_share_extract" or "memory_sweep"
            or "memory_set_ttl" or "code_get" or "memory_delete_context";
}
