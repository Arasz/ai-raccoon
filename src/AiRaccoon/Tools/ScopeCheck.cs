using AiRaccoon.Core.Projects;

namespace AiRaccoon.Tools;

/// <summary>Combines the MCP tool classification with the shared project policy.</summary>
public sealed class ScopeCheck(IToolNameScope tools, ISharedProjectScope projects) : IScopeCheck
{
    /// <inheritdoc />
    public ScopeCheckResult Check(string canonicalProjectId, string toolName) =>
        new(tools.RequiresOrdinaryProject(toolName)
            ? projects.CheckOrdinary(canonicalProjectId, toolName)
            : null);
}
