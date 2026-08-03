using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Common;

/// <summary>Builds the context strings that partition memory inside the bank.</summary>
public static class ContextNaming
{
    public const string SharedContext = "shared";

    public static string ProjectContext(string projectId)
    {
        Guard.IsNotNullOrWhiteSpace(projectId, nameof(projectId));
        return $"project:{projectId}";
    }

    public static string WorkspaceContext(string workspaceId)
    {
        Guard.IsNotNullOrWhiteSpace(workspaceId, nameof(workspaceId));
        return $"workspace:{workspaceId}";
    }
}
