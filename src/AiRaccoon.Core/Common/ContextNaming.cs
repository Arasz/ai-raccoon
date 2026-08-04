using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Common;

/// <summary>Builds the context strings that partition memory inside the bank.</summary>
public static class ContextNaming
{
    public const string SharedContext = "shared";

    public static string ProjectContext(string projectId)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        return $"project:{projectId}";
    }

    public static string WorkspaceContext(string workspaceId)
    {
        Guard.IsNotNullOrWhiteSpace(workspaceId);
        return $"workspace:{workspaceId}";
    }

    /// <summary>Context string selecting a project's custom-scoped rows under one label.</summary>
    public static string LabelContext(string projectId, string contextLabel)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        Guard.IsNotNullOrWhiteSpace(contextLabel);
        return $"label:{projectId}:{contextLabel}";
    }
}
