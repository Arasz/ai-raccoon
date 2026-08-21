namespace AiRaccoon.Core.Memory;

/// <summary>
///     Owns memory_search's `kind` selection (docs/work/2026-08-21-code-search-implementation-plan.md
///     §3.6): which of the memory/code legs run, the code leg's project-scoped-only rule (empty on
///     scope=shared) and its FTS5-only-mode warning, and the search_quality recording exclusion for
///     kind=code/both — so the MCP tool layer stays a thin caller (mcp.instructions.md).
/// </summary>
public interface ISearchDispatcher
{
    Task<SearchDispatchResult> DispatchAsync(SearchQuery searchQuery, SearchKind kind, string rawScope,
        string correlationId, CancellationToken cancellationToken = default);
}
