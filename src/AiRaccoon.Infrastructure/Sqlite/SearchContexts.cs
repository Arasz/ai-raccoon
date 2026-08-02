using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>The bank contexts a search query reads, per scope (spec §4.1): one memory_search query per in-scope context.</summary>
internal static class SearchContexts
{
    public static IReadOnlyList<string> For(SearchQuery query)
    {
        var contexts = new List<string>();

        if (query.Scope is SearchScope.All or SearchScope.Shared)
        {
            contexts.Add(ContextNaming.SharedContext);
        }

        if (query.Scope is SearchScope.All or SearchScope.Project)
        {
            contexts.Add(ContextNaming.ProjectContext(query.ProjectId));
        }

        if (!string.IsNullOrWhiteSpace(query.WorkspaceId) && query.Scope is not SearchScope.Shared)
        {
            contexts.Add(ContextNaming.WorkspaceContext(query.WorkspaceId));
        }

        return contexts;
    }
}
