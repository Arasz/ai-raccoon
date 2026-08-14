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
            var named = context["project:".Length..];
            if (!string.Equals(named, projectId, StringComparison.Ordinal))
            {
                throw new ContextOutsideProjectException(context, projectId);
            }

            return ("project", named, null, null);
        }

        if (context.StartsWith("workspace:", StringComparison.Ordinal))
        {
            return (null, projectId, null, context["workspace:".Length..]);
        }

        // Mirrors MemorySql.ContextKeyFor: the label's project segment is stripped, so the text
        // column and the vec0 partition key agree on what the label is.
        if (context.StartsWith("label:", StringComparison.Ordinal))
        {
            var rest = context["label:".Length..];
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var named = rest[..colon];
                if (!string.Equals(named, projectId, StringComparison.Ordinal))
                {
                    throw new ContextOutsideProjectException(context, projectId);
                }

                return ("custom", projectId, rest[(colon + 1)..], null);
            }
        }

        return ("custom", projectId, context, null);
    }
}
