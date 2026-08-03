using System.ComponentModel;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IMemoryStore and the workspace/sweep/sync services — no business logic here (spec §6.1).</summary>
public sealed class MemoryTools(IMemoryStore store, SyncService sync, WorkspaceService workspaces, SweepService sweeper)
{
    private static void RequireProjectId(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new McpException("invalid-params: project_id is required");
        }
    }

    [McpServerTool(Name = "memory_write")]
    [Description(
        "Writes content into memory. Writes land in the project's committed context by default; naming a workspace_id routes them into that isolated workspace. Returns the stored entry.")]
    public async Task<WriteResult> Write(
        [Description("The project id; every memory operation is scoped to a project.")]
        string projectId,
        [Description("The content to remember.")]
        string content,
        [Description("When set, the write lands in this workspace's isolated context instead of the project context.")]
        string? workspaceId = null,
        [Description("Provenance only: which agent wrote this.")]
        string? agentId = null,
        [Description("Optional custom context label instead of the default project/workspace context.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(content, nameof(content));

        var entry = await store.WriteAsync(
            new MemoryWriteRequest(projectId, content, context, agentId, workspaceId), cancellationToken);
        return new WriteResult(entry.Hash, entry.Path, entry.Context, entry.CreatedAt);
    }

    [McpServerTool(Name = "memory_search")]
    [Description(
        "Hybrid semantic search over the bank. scope=all (default) searches shared + project (+ workspace when named); scope=project searches the project only; scope=shared searches the shared promotion tier only.")]
    public async Task<SearchResultList> Search(
        [Description("The project id.")] string projectId,
        [Description("The search query.")] string query,
        [Description("Search scope: all (default), project, or shared.")]
        string scope = "all",
        [Description("When set, also searches this workspace's isolated context.")]
        string? workspaceId = null,
        [Description("Maximum results (default 20).")]
        int limit = 20,
        [Description("Minimum ranking threshold 0..1 (default 0.7).")]
        double minScore = 0.7,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));

        var parsedScope = scope.ToLowerInvariant() switch
        {
            "all" => SearchScope.All,
            "project" => SearchScope.Project,
            "shared" => SearchScope.Shared,
            _ => throw new McpException($"Invalid scope '{scope}': expected all, project, or shared.")
        };

        var results = await store.SearchAsync(
            new SearchQuery(projectId, query, parsedScope, workspaceId, limit, minScore), cancellationToken);
        return new SearchResultList(results);
    }

    [McpServerTool(Name = "memory_list")]
    [Description("Lists the bank's indexed files as a JSON tree (memory_list_files).")]
    public async Task<ListResult> List(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        var files = await store.ListFilesAsync(projectId, cancellationToken);
        return new ListResult(files);
    }

    [McpServerTool(Name = "memory_stats")]
    [Description("Reports entry count, pending (deferred-embedding) count, and the bank's committed contexts.")]
    public async Task<StatsResult> Stats(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        var stats = await store.GetStatsAsync(projectId, cancellationToken);
        return new StatsResult(stats.EntryCount, stats.PendingCount, stats.Contexts);
    }

    [McpServerTool(Name = "memory_share")]
    [Description(
        "Promotes an existing project entry into the flat shared context — the curated, cross-project, sweep-exempt tier. Nothing is shared without this explicit promotion.")]
    public async Task<ShareResult> Share(
        [Description("The project id.")] string projectId,
        [Description("The content hash to promote.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash, nameof(hash));

        var entry = await store.ShareAsync(projectId, hash, cancellationToken);
        return new ShareResult(true, entry.Context);
    }

    [McpServerTool(Name = "memory_delete")]
    [Description("Deletes a specific memory entry by its content hash.")]
    public async Task<DeletedResult> Delete(
        [Description("The project id.")] string projectId,
        [Description("The content hash to delete.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash, nameof(hash));

        var deleted = await store.DeleteAsync(projectId, hash, cancellationToken);
        return new DeletedResult(deleted ? 1 : 0);
    }

    [McpServerTool(Name = "memory_delete_context")]
    [Description("Deletes every entry stored under a context label (e.g. a project or workspace context).")]
    public async Task<DeletedContextResult> DeleteContext(
        [Description("The project id.")] string projectId,
        [Description("The context label to delete.")]
        string context,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context, nameof(context));

        var deleted = await store.DeleteContextAsync(projectId, context, cancellationToken);
        return new DeletedContextResult(deleted);
    }

    [McpServerTool(Name = "memory_ingest_file")]
    [Description("Indexes one file from disk into memory.")]
    public async Task<IngestResult> IngestFile(
        [Description("The project id.")] string projectId,
        [Description("Path of the file to index.")]
        string path,
        [Description("Optional context label.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));

        var indexed = await store.IngestFileAsync(projectId, path, context, cancellationToken);
        return new IngestResult(indexed);
    }

    [McpServerTool(Name = "memory_ingest_directory")]
    [Description("Recursively indexes a directory tree into memory, skipping unchanged files.")]
    public async Task<ScannedResult> IngestDirectory(
        [Description("The project id.")] string projectId,
        [Description("Path of the directory to index.")]
        string path,
        [Description("Optional context label applied to all files.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));

        var scanned = await store.IngestDirectoryAsync(projectId, path, context, cancellationToken);
        return new ScannedResult(scanned);
    }

    [McpServerTool(Name = "memory_configure")]
    [Description(
        "Configures the bank's embedding model: provider 'local' with a GGUF model path, or a remote provider name with a model id. Remote requires an API key (env AIRACCOON_VECTORSSPACE_API_KEY or api_key).")]
    public async Task<ConfigureResult> Configure(
        [Description("The project id.")] string projectId,
        [Description("Embedding provider: 'local' or a remote provider name.")]
        string provider,
        [Description("GGUF model path (local) or remote model id.")]
        string model,
        [Description("Optional API key for remote embeddings; never persisted.")]
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);

        var isRemote = !string.Equals(provider, "local", StringComparison.OrdinalIgnoreCase);
        var resolvedKey = apiKey ?? Environment.GetEnvironmentVariable("AIRACCOON_VECTORSSPACE_API_KEY");
        if (isRemote && string.IsNullOrWhiteSpace(resolvedKey))
        {
            throw new McpException(
                "embedding-api-key-missing: set AIRACCOON_VECTORSSPACE_API_KEY or pass api_key for a remote embedding provider");
        }

        var config = await store.ConfigureEmbeddingAsync(projectId, provider, model, resolvedKey, cancellationToken);
        return new ConfigureResult(config.Provider, config.Model, config.Engine);
    }

    [McpServerTool(Name = "memory_embed_pending")]
    [Description("Embeds deferred entries in batches (used when no model was configured at write time).")]
    public async Task<EmbedResult> EmbedPending(
        [Description("The project id.")] string projectId,
        [Description("Maximum rows to process in this call; omit for all.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);

        var result = await store.EmbedPendingAsync(projectId, limit, cancellationToken);
        return new EmbedResult(result.Processed, result.Pending);
    }

    [McpServerTool(Name = "memory_workspace_begin")]
    [Description(
        "Begins a workspace sandbox: returns a workspace_id whose context is isolated by design. While it is active, write with that workspace_id so notes stay in the outbox.")]
    public async Task<WorkspaceBeginResult> WorkspaceBegin(
        [Description("The project id.")] string projectId,
        [Description("Provenance only: which agent is working in this workspace.")]
        string? agentId = null,
        [Description("Optional human-readable workspace name.")]
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);

        var workspace = await workspaces.BeginAsync(projectId, cancellationToken);
        return new WorkspaceBeginResult(workspace.Id, workspace.Context);
    }

    [McpServerTool(Name = "memory_workspace_status")]
    [Description("Lists the entries currently in a workspace's outbox.")]
    public async Task<WorkspaceStatusResult> WorkspaceStatus(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId, nameof(workspaceId));

        var entries = await workspaces.GetStatusAsync(projectId, workspaceId, cancellationToken);
        return new WorkspaceStatusResult(entries, entries.Count);
    }

    [McpServerTool(Name = "memory_workspace_consolidate")]
    [Description(
        "Finishes a workspace: promotes the kept hashes (or 'all') from the workspace outbox into the project's committed memory, then removes the workspace context.")]
    public async Task<ConsolidationToolResult> WorkspaceConsolidate(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        [Description("Hashes to promote, or ['all'] to promote everything.")]
        string[] keep,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId, nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(keep, nameof(keep));

        var result = await workspaces.ConsolidateAsync(projectId, workspaceId, keep, cancellationToken);
        return new ConsolidationToolResult(result.Promoted, result.Discarded);
    }

    [McpServerTool(Name = "memory_workspace_discard")]
    [Description("Discards a workspace without promoting anything: removes its outbox context and all its entries.")]
    public async Task<DeletedContextResult> WorkspaceDiscard(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId, nameof(workspaceId));

        var discarded = await workspaces.DiscardAsync(projectId, workspaceId, cancellationToken);
        return new DeletedContextResult(discarded);
    }

    [McpServerTool(Name = "memory_sweep")]
    [Description(
        "Runs memory degradation: lists (dry_run, default) or deletes entries whose rating is below the threshold and older than the TTL. Shared entries are never swept.")]
    public async Task<SweepResult> Sweep(
        [Description("The project id.")] string projectId,
        [Description("When true (default), report candidates without deleting.")]
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);

        var outcome = await sweeper.SweepAsync(projectId, 0.3, 30, dryRun, cancellationToken);
        return new SweepResult(outcome.Candidates, outcome.DeletedHashes);
    }

    [McpServerTool(Name = "memory_sync")]
    [Description(
        "Syncs the bank's committed contexts (shared + project:<id>) to the configured cloud database. Requires AIRACCOON_SQLITECLOUD_DB_ID and AIRACCOON_SQLITECLOUD_API_KEY.")]
    public async Task<SyncToolResult> Sync(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        RequireProjectId(projectId);

        try
        {
            var result = await sync.MemorySyncAsync(projectId, cancellationToken);
            return new SyncToolResult(result.Sent, result.Received, result.Reindexed);
        }
        catch (SyncNotConfiguredException)
        {
            throw new McpException(
                "sync-not-configured: set AIRACCOON_SQLITECLOUD_DB_ID and AIRACCOON_SQLITECLOUD_API_KEY");
        }
    }

    public sealed record WriteResult(string Hash, string Path, string Context, long CreatedAt);

    public sealed record SearchResultList(IReadOnlyList<MemorySearchResult> Results);

    public sealed record ListResult(string Files);

    public sealed record StatsResult(int Entries, int Pending, IReadOnlyList<string> Contexts);

    public sealed record ShareResult(bool Shared, string Context);

    public sealed record DeletedResult(int Deleted);

    public sealed record DeletedContextResult(int Deleted);

    public sealed record IngestResult(int Indexed);

    public sealed record ScannedResult(int Scanned);

    public sealed record ConfigureResult(string Provider, string Model, string Engine);

    public sealed record EmbedResult(int Processed, int Pending);

    public sealed record WorkspaceBeginResult(string WorkspaceId, string Context);

    public sealed record WorkspaceStatusResult(IReadOnlyList<MemoryEntry> Entries, int Count);

    public sealed record ConsolidationToolResult(int Promoted, int Discarded);

    public sealed record SweepResult(IReadOnlyList<SweepCandidate> Candidates, IReadOnlyList<string> Deleted);

    public sealed record SyncToolResult(int Sent, int Received, int Reindexed);
}
