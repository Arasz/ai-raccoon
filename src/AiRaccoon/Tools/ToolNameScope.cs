namespace AiRaccoon.Tools;

/// <summary>
///     The MCP tool scope, derived from the declarations the generator compiles (ADR-0139). The
///     tool surface is the authority: a name that is not a declared tool is refused, because the
///     map knows every tool and a name outside it names no tool at all.
/// </summary>
public sealed class ToolNameScope : IToolNameScope
{
    /// <inheritdoc />
    public bool RequiresOrdinaryProject(string toolName) =>
        !McpToolScopeGenerated.RequiresOrdinaryProjectByTool.TryGetValue(toolName, out var requiresOrdinaryProject)
        || requiresOrdinaryProject;
}
