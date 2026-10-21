using AiRaccoon.Core.Projects;

namespace AiRaccoon.Tools;

/// <summary>Combines the MCP tool classification with the shared project policy.</summary>
public sealed class ScopeCheck(IToolNameScope tools, ISharedProjectScope projects) : IScopeCheck
{
    /// <inheritdoc />
    public ScopeCheckResult Check(string canonicalProjectId, string toolName)
    {
        var scope = tools.ScopeOf(toolName);
        var target = projects.IsReserved(canonicalProjectId) ? ToolScope.Shared : ToolScope.Project;
        return new ScopeCheckResult(scope == ToolScope.All || scope == target);
    }
}
