using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     Maps a context string to a row filter — the read/delete twin of <see cref="EntryBucket"/>,
///     which maps the same strings to write-time bucket columns. They live together so the rule
///     that a context stays inside its project cannot hold on one side and not the other.
/// </summary>
internal static class ContextFilter
{
    /// <summary>
    ///     Built only from constant fragments; every value goes through parameters, so a user-supplied
    ///     context string can never inject SQL. Callers that accept an untrusted context must first
    ///     pass it through <see cref="ContextScope.RequireWithinProject"/> — this maps, it does not
    ///     authorize.
    /// </summary>
    public static ContextFilterValues For(string context, string projectId, string alias)
    {
        if (context == ContextNaming.SharedContext)
        {
            return new ContextFilterValues($"{alias}scope = 'shared'", []);
        }

        if (context.StartsWith("project:", StringComparison.Ordinal))
        {
            return new ContextFilterValues($"{alias}scope = 'project' AND {alias}project_id = @projectId",
                new Dictionary<string, object?> { ["projectId"] = projectId });
        }

        if (context.StartsWith("workspace:", StringComparison.Ordinal))
        {
            return new ContextFilterValues($"{alias}workspace_id = @workspaceId AND {alias}project_id = @projectId",
                new Dictionary<string, object?> { ["workspaceId"] = context["workspace:".Length..], ["projectId"] = projectId });
        }

        if (context.StartsWith("label:", StringComparison.Ordinal))
        {
            var rest = context["label:".Length..];
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var label = rest[(colon + 1)..];
                return new ContextFilterValues(
                    $"{alias}scope = 'custom' AND {alias}context_label = @contextLabel AND {alias}project_id = @projectId",
                    new Dictionary<string, object?> { ["projectId"] = projectId, ["contextLabel"] = label });
            }
        }

        return new ContextFilterValues($"{alias}scope = 'custom' AND {alias}context_label = @contextLabel AND {alias}project_id = @projectId",
            new Dictionary<string, object?> { ["contextLabel"] = context, ["projectId"] = projectId });
    }
}

public sealed record ContextFilterValues(string Filter, Dictionary<string, object?> Values);
