namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     The code corpus's read surface: FTS5-only in this wave (v1 of the WP5 hybrid — the seam
///     where the vec0 leg + RRF fusion + manifest-aware query trim land later, docs/work/2026-08-21-code-search-implementation-plan.md §3.6).
///     Always project-scoped (§3.1: code has no shared/workspace tier).
/// </summary>
public interface ICodeSearchService
{
    Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>Mirrors IMemoryStore.GetAsync; null when the project owns no such hash.</summary>
    Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default);
}
