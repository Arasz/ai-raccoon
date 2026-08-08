using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Maps a context string to the entries-table bucket columns (scope/project/label/workspace).</summary>
internal static class EntryBucket
{
    public static (string? Scope, string ProjectId, string? ContextLabel, string? WorkspaceId) For(
        string context, string projectId)
    {
        if (context == ContextNaming.SharedContext)
        {
            return ("shared", projectId, null, null);
        }

        if (context.StartsWith("project:", StringComparison.Ordinal))
        {
            return ("project", context["project:".Length..], null, null);
        }

        if (context.StartsWith("workspace:", StringComparison.Ordinal))
        {
            return (null, projectId, null, context["workspace:".Length..]);
        }

        return ("custom", projectId, context, null);
    }
}
