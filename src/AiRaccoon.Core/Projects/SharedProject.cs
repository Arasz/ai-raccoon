namespace AiRaccoon.Core.Projects;

/// <summary>The explicit, built-in target for documents shared across one memory bank.</summary>
public static class SharedProject
{
    public const string Id = "shared";
    public const string WatchActivationKey = "shared.documents.watch-owner";

    public static bool Is(string? projectId) => string.Equals(projectId, Id, StringComparison.Ordinal);

    public static void RequireSearch(AiRaccoon.Core.Memory.SearchQuery query)
    {
        if (Is(query.ProjectId) && (query.Scope == AiRaccoon.Core.Memory.SearchScope.Project
            || !string.IsNullOrWhiteSpace(query.WorkspaceId) || !string.IsNullOrWhiteSpace(query.ContextLabel)))
        {
            throw new ArgumentException("shared-project-unsupported: search shared with scope all or shared, without a workspace or label.");
        }
    }

    public static void RequireOrdinary(string projectId, string operation)
    {
        if (Is(projectId))
        {
            throw new ArgumentException($"shared-project-unsupported: {operation} requires an ordinary project; shared supports document ingestion and watches.");
        }
    }
}
