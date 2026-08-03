using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Workspace;
using WorkspaceRecord = AiRaccoon.Core.Workspace.Workspace;

namespace AiRaccoon.Infrastructure.Workspace;

/// <summary>Workspace lifecycle orchestration over IMemoryStore: begin, status, consolidate (outbox → project inbox), discard (spec §3.2).</summary>
public sealed class WorkspaceService(IMemoryStore store)
{
    private readonly IMemoryStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<WorkspaceRecord> BeginAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        // v7 (sortable, time-ordered) ids make workspace lists deterministic by creation order.
        var id = Guid.CreateVersion7().ToString("N");
        return Task.FromResult(new WorkspaceRecord(id, projectId));
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetStatusAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var context = ContextNaming.WorkspaceContext(workspaceId);
        return await _store.ListContextAsync(projectId, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConsolidationResult> ConsolidateAsync(
        string projectId, string workspaceId, IReadOnlyList<string> keep, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(keep);

        var workspaceContext = ContextNaming.WorkspaceContext(workspaceId);
        var entries = await _store.ListContextAsync(projectId, workspaceContext, cancellationToken)
            .ConfigureAwait(false);
        var byHash = entries.ToDictionary(e => e.Hash, e => e, StringComparer.Ordinal);

        var keepAll = keep.Count == 1 && string.Equals(keep[0], "all", StringComparison.OrdinalIgnoreCase);
        var hashes = keepAll ? byHash.Keys.ToList() : keep.Where(byHash.ContainsKey).ToList();

        var promoted = 0;
        foreach (var hash in hashes)
        {
            var entry = byHash[hash];
            // Promote via memory_add_content(path, value, 'project:<id>') — preserves the
            // entry's logical path and lands it in the project context (spec §3.2). Using
            // add_content rather than add_text avoids the global content-hash dedup, which
            // would skip content that already exists in the workspace context.
            await _store.AddContentAsync(
                projectId, entry.Path, entry.Value, ContextNaming.ProjectContext(projectId),
                cancellationToken).ConfigureAwait(false);
            promoted++;
        }

        var discarded = await _store.DeleteContextAsync(projectId, workspaceContext, cancellationToken)
            .ConfigureAwait(false);
        return new ConsolidationResult(promoted, discarded);
    }

    public async Task<int> DiscardAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var context = ContextNaming.WorkspaceContext(workspaceId);
        return await _store.DeleteContextAsync(projectId, context, cancellationToken).ConfigureAwait(false);
    }
}
