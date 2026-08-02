namespace AiRaccon.Core.Common;

/// <summary>Builds the context strings that partition memory inside a project database.</summary>
public static class ContextNaming
{
    public static string ProjectContext(string projectId)
    {
        Guard.NotNullOrWhiteSpace(projectId, nameof(projectId));
        return $"project:{projectId}";
    }

    public static string WorkspaceContext(string workspaceId)
    {
        Guard.NotNullOrWhiteSpace(workspaceId, nameof(workspaceId));
        return $"workspace:{workspaceId}";
    }
}
