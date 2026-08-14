using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     The one place that decides whether a context string stays inside the caller's project.
///     Both the write bucket and the delete path call it, so the rule cannot drift between them.
/// </summary>
public static class ContextScope
{
    /// <summary>
    ///     Throws <see cref="ContextOutsideProjectException"/> unless <paramref name="context"/> targets
    ///     <paramref name="projectId"/>. The shared tier belongs to no project, so it is outside every one.
    /// </summary>
    public static void RequireWithinProject(string context, string projectId)
    {
        Guard.IsNotNullOrWhiteSpace(context);
        Guard.IsNotNullOrWhiteSpace(projectId);

        if (context == ContextNaming.SharedContext)
        {
            throw new ContextOutsideProjectException(context, projectId);
        }

        var named = NamedProject(context);
        if (named is not null && !string.Equals(named, projectId, StringComparison.Ordinal))
        {
            throw new ContextOutsideProjectException(context, projectId);
        }
    }

    /// <summary>The project a context names explicitly, or null when it carries no project segment.</summary>
    public static string? NamedProject(string context)
    {
        Guard.IsNotNullOrWhiteSpace(context);

        if (context.StartsWith("project:", StringComparison.Ordinal))
        {
            return context["project:".Length..];
        }

        if (!context.StartsWith("label:", StringComparison.Ordinal))
        {
            return null;
        }

        // `label:<projectId>:<label>` — a bare `label:x` with no second colon carries no project segment.
        var rest = context["label:".Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? rest[..colon] : null;
    }
}
