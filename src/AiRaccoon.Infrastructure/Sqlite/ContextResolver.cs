using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Resolves the context string for a write: the workspace wins (the sandbox has priority, owner ruling K5), then an explicit context, then the project.</summary>
internal static class ContextResolver
{
    public static string Resolve(MemoryWriteRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            return ContextNaming.WorkspaceContext(request.WorkspaceId);
        }

        return string.IsNullOrWhiteSpace(request.Context)
            ? ContextNaming.ProjectContext(request.ProjectId)
            : request.Context;
    }
}
