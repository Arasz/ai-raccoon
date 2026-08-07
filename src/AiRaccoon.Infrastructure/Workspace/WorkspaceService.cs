using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Workspace;
using WorkspaceRecord = AiRaccoon.Core.Workspace.Workspace;

namespace AiRaccoon.Infrastructure.Workspace;

/// <summary>
///     Workspace lifecycle orchestration over IMemoryStore: begin, status, consolidate (outbox → project inbox),
///     discard (see docs/work/features-agent-memory/spec-issue-1.md §3.2).
/// </summary>
public sealed class WorkspaceService(IMemoryStore store, IWorkspaceStore workspaceStore, TimeProvider timeProvider)
{
    public async Task<WorkspaceRecord> BeginAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        // v7 (sortable, time-ordered) ids make workspace lists deterministic by creation order.
        var id = Guid.CreateVersion7().ToString("N");
        var workspace = new WorkspaceRecord(id, projectId);
        await workspaceStore.BeginAsync(projectId, id, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return workspace;
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetStatusAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var context = ContextNaming.WorkspaceContext(workspaceId);
        return await store.ListContextAsync(projectId, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConsolidationResult> ConsolidateAsync(
        string projectId, string workspaceId, IReadOnlyList<string> keep, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(keep);

        var workspaceContext = ContextNaming.WorkspaceContext(workspaceId);
        var entries = await store.ListContextAsync(projectId, workspaceContext, cancellationToken)
            .ConfigureAwait(false);
        var byHash = entries.ToDictionary(e => e.Hash, e => e, StringComparer.Ordinal);

        var keepAll = keep.Count == 1 && string.Equals(keep[0], "all", StringComparison.OrdinalIgnoreCase);
        var hashes = keepAll ? byHash.Keys.ToList() : keep.Where(byHash.ContainsKey).ToList();

        var promoted = 0;
        foreach (var hash in hashes)
        {
            var entry = byHash[hash];
            // Promote via memory_add_content(path, value, 'project:<id>') — preserves the
            // entry's logical path and lands it in the project context (see docs/work/features-agent-memory/spec-issue-1.md §3.2). Using
            // add_content rather than add_text avoids the global content-hash dedup, which
            // would skip content that already exists in the workspace context.
            await store.AddContentAsync(
                projectId, entry.Path, entry.Value, ContextNaming.ProjectContext(projectId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            promoted++;
        }

        var discarded = await store.DeleteContextAsync(projectId, workspaceContext, cancellationToken)
            .ConfigureAwait(false);
        await workspaceStore.CloseAsync(projectId, workspaceId, WorkspaceStatus.Closed, timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return new ConsolidationResult(promoted, discarded);
    }

    public async Task<int> DiscardAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var context = ContextNaming.WorkspaceContext(workspaceId);
        var discarded = await store.DeleteContextAsync(projectId, context, cancellationToken).ConfigureAwait(false);
        await workspaceStore.CloseAsync(projectId, workspaceId, WorkspaceStatus.Closed, timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return discarded;
    }
}
