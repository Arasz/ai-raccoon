using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Resolves the context string for a write: explicit context wins, then workspace, then project.</summary>
internal static class ContextResolver
{
    public static string Resolve(MemoryWriteRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            return request.Context;
        }

        return string.IsNullOrWhiteSpace(request.WorkspaceId)
            ? ContextNaming.ProjectContext(request.ProjectId)
            : ContextNaming.WorkspaceContext(request.WorkspaceId);
    }
}
