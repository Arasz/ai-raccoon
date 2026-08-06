using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using FluentValidation;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IMemoryStore and the workspace/sweep/sync services — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class MemoryTools(
    IMemoryStore store,
    SyncService sync,
    WorkspaceService workspaces,
    SweepService sweeper,
    IMemoryAccessGuard access,
    SyncCloudStoreFactory syncFactory,
    ForgettingPolicyService knobs,
    ToolCallMetrics observability)
{
    // ── MCP tool name constants ──
    private const string TnMemoryWrite = "memory_write";
    private const string TnMemorySearch = "memory_search";
    private const string TnMemoryList = "memory_list";
    private const string TnMemoryStats = "memory_stats";
    private const string TnMemoryShare = "memory_share";
    private const string TnMemoryDelete = "memory_delete";
    private const string TnMemoryDeleteContext = "memory_delete_context";
    private const string TnMemoryIngestFile = "memory_ingest_file";
    private const string TnMemoryIngestDirectory = "memory_ingest_directory";
    private const string TnMemoryEmbedPending = "memory_embed_pending";
    private const string TnMemoryWorkspaceBegin = "memory_workspace_begin";
    private const string TnMemoryWorkspaceStatus = "memory_workspace_status";
    private const string TnMemoryWorkspaceConsolidate = "memory_workspace_consolidate";
    private const string TnMemoryWorkspaceDiscard = "memory_workspace_discard";
    private const string TnMemorySweep = "memory_sweep";
    private const string TnMemorySync = "memory_sync";

    private const string ToolActivityTag = "tool";
    private const string ProjectIdActivityTag = "project_id";
    private static readonly SearchQuery.Validator SearchQueryValidator = new();
    private static readonly MemoryWriteRequest.Validator MemoryWriteRequestValidator = new();


    private static void RequireProjectId(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new McpException("invalid-params: project_id is required");
        }
    }

    private async Task RequireAsync(string projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken) =>
        await access.EnsureAsync(projectId, requirement, toolName, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = TnMemoryWrite)]
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
        [Description("Optional original file path the content came from; chunks of one file share it.")]
        string? sourceFile = null,
        [Description("Optional section slug within the source file (e.g. 'decision'); indexed as a weighted FTS column.")]
        string? section = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryWrite);
        activity?.SetTag(ToolActivityTag, TnMemoryWrite);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryWrite, cancellationToken);

            var request = new MemoryWriteRequest(projectId, content, context, agentId, workspaceId, sourceFile, section);
            await MemoryWriteRequestValidator.ValidateAndThrowAsync(request, cancellationToken);

            var entry = await store.WriteAsync(request, cancellationToken);
            var result = new WriteResult(entry.Hash, entry.Path, entry.Context, entry.CreatedAt);
            observability.RecordInvocation(TnMemoryWrite, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryWrite, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemorySearch)]
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
        [Description("RRF cutoff for the hybrid fusion (default 60); a result scores weight / (k + rank) per modality list.")]
        int rrfK = SearchQuery.DefaultRrfK,
        [Description("Weight of the keyword (FTS5) list in the RRF fusion (default 1).")]
        int ftsWeight = 1,
        [Description("Weight of the semantic (vector) list in the RRF fusion (default 1).")]
        int vectorWeight = 1,
        [Description("When set, the project scope also searches custom-scoped rows under this context label.")]
        string? contextLabel = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemorySearch);
        activity?.SetTag(ToolActivityTag, TnMemorySearch);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TnMemorySearch, cancellationToken);

            var parsedScope = scope.ToLowerInvariant() switch
            {
                "all" => SearchScope.All,
                "project" => SearchScope.Project,
                "shared" => SearchScope.Shared,
                _ => throw new McpException($"Invalid scope '{scope}': expected all, project, or shared.")
            };

            var searchQuery = new SearchQuery(projectId, query, parsedScope, workspaceId, limit, minScore,
                rrfK, ftsWeight, vectorWeight, contextLabel);

            await SearchQueryValidator.ValidateAndThrowAsync(searchQuery, cancellationToken);

            var results = await store.SearchAsync(searchQuery, cancellationToken);
            var result = new SearchResultList(results);
            observability.RecordInvocation(TnMemorySearch, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemorySearch, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryList)]
    [Description("Lists the bank's indexed files as a JSON tree (memory_list_files).")]
    public async Task<ListResult> List(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryList, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TnMemoryList, cancellationToken);
            var files = await store.ListFilesAsync(projectId, cancellationToken);
            var result = new ListResult(JsonNode.Parse(files) ?? new JsonObject());
            activity.RecordInvocation();
            return result;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryStats)]
    [Description("Reports entry count, pending (deferred-embedding) count, and the bank's committed contexts.")]
    public async Task<StatsResult> Stats(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryStats);
        activity?.SetTag(ToolActivityTag, TnMemoryStats);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TnMemoryStats, cancellationToken);
            var stats = await store.GetStatsAsync(projectId, cancellationToken);
            var result = new StatsResult(stats.EntryCount, stats.PendingCount, stats.Contexts);
            observability.RecordInvocation(TnMemoryStats, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryStats, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryShare)]
    [Description(
        "Promotes an existing project entry into the flat shared context — the curated, cross-project, sweep-exempt tier. Nothing is shared without this explicit promotion.")]
    public async Task<ShareResult> Share(
        [Description("The project id.")] string projectId,
        [Description("The content hash to promote.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryShare);
        activity?.SetTag(ToolActivityTag, TnMemoryShare);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryShare, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);

            var entry = await store.ShareAsync(projectId, hash, cancellationToken);
            var result = new ShareResult(true, entry.Context);
            observability.RecordInvocation(TnMemoryShare, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryShare, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryDelete)]
    [Description("Deletes a specific memory entry by its content hash.")]
    public async Task<DeletedResult> Delete(
        [Description("The project id.")] string projectId,
        [Description("The content hash to delete.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryDelete);
        activity?.SetTag(ToolActivityTag, TnMemoryDelete);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryDelete, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);

            var deleted = await store.DeleteAsync(projectId, hash, cancellationToken);
            var result = new DeletedResult(deleted ? 1 : 0);
            observability.RecordInvocation(TnMemoryDelete, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryDelete, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryDeleteContext)]
    [Description("Deletes every entry stored under a context label (e.g. a project or workspace context).")]
    public async Task<DeletedContextResult> DeleteContext(
        [Description("The project id.")] string projectId,
        [Description("The context label to delete.")]
        string context,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryDeleteContext);
        activity?.SetTag(ToolActivityTag, TnMemoryDeleteContext);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryDeleteContext, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(context);

            var deleted = await store.DeleteContextAsync(projectId, context, cancellationToken);
            var result = new DeletedContextResult(deleted);
            observability.RecordInvocation(TnMemoryDeleteContext, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryDeleteContext, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryIngestFile)]
    [Description("Indexes one file from disk into memory.")]
    public async Task<IngestResult> IngestFile(
        [Description("The project id.")] string projectId,
        [Description("Path of the file to index.")]
        string path,
        [Description("Optional context label.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryIngestFile);
        activity?.SetTag(ToolActivityTag, TnMemoryIngestFile);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryIngestFile, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var indexed = await store.IngestFileAsync(projectId, path, context, cancellationToken);
            var result = new IngestResult(indexed);
            observability.RecordInvocation(TnMemoryIngestFile, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryIngestFile, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryIngestDirectory)]
    [Description("Recursively indexes a directory tree into memory, skipping unchanged files.")]
    public async Task<ScannedResult> IngestDirectory(
        [Description("The project id.")] string projectId,
        [Description("Path of the directory to index.")]
        string path,
        [Description("Optional context label applied to all files.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryIngestDirectory);
        activity?.SetTag(ToolActivityTag, TnMemoryIngestDirectory);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryIngestDirectory, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var scanned = await store.IngestDirectoryAsync(projectId, path, context, cancellationToken);
            var result = new ScannedResult(scanned);
            observability.RecordInvocation(TnMemoryIngestDirectory, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryIngestDirectory, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }


    [McpServerTool(Name = TnMemoryEmbedPending)]
    [Description("Embeds deferred entries in batches (used when no model was configured at write time).")]
    public async Task<EmbedResult> EmbedPending(
        [Description("The project id.")] string projectId,
        [Description("Maximum rows to process in this call; omit for all.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryEmbedPending);
        activity?.SetTag(ToolActivityTag, TnMemoryEmbedPending);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryEmbedPending, cancellationToken);

            var result = await store.EmbedPendingAsync(projectId, limit, cancellationToken);
            var embedResult = new EmbedResult(result.Processed, result.Pending);
            observability.RecordInvocation(TnMemoryEmbedPending, sw.Elapsed, false);
            return embedResult;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryEmbedPending, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryWorkspaceBegin)]
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
        using var activity = observability.ActivitySource.StartActivity(TnMemoryWorkspaceBegin);
        activity?.SetTag(ToolActivityTag, TnMemoryWorkspaceBegin);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryWorkspaceBegin, cancellationToken);

            var workspace = await workspaces.BeginAsync(projectId, cancellationToken);
            var result = new WorkspaceBeginResult(workspace.Id, workspace.Context);
            observability.RecordInvocation(TnMemoryWorkspaceBegin, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryWorkspaceBegin, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryWorkspaceStatus)]
    [Description("Lists the entries currently in a workspace's outbox.")]
    public async Task<WorkspaceStatusResult> WorkspaceStatus(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryWorkspaceStatus);
        activity?.SetTag(ToolActivityTag, TnMemoryWorkspaceStatus);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TnMemoryWorkspaceStatus, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

            var entries = await workspaces.GetStatusAsync(projectId, workspaceId, cancellationToken);
            var result = new WorkspaceStatusResult(entries, entries.Count);
            observability.RecordInvocation(TnMemoryWorkspaceStatus, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryWorkspaceStatus, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryWorkspaceConsolidate)]
    [Description(
        "Finishes a workspace: promotes the kept hashes (or 'all') from the workspace outbox into the project's committed memory, then removes the workspace context.")]
    public async Task<ConsolidationToolResult> WorkspaceConsolidate(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        [Description("Hashes to promote, or ['all'] to promote everything.")]
        string[] keep,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryWorkspaceConsolidate);
        activity?.SetTag(ToolActivityTag, TnMemoryWorkspaceConsolidate);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryWorkspaceConsolidate, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
            ArgumentNullException.ThrowIfNull(keep);

            var result = await workspaces.ConsolidateAsync(projectId, workspaceId, keep, cancellationToken);
            var toolResult = new ConsolidationToolResult(result.Promoted, result.Discarded);
            observability.RecordInvocation(TnMemoryWorkspaceConsolidate, sw.Elapsed, false);
            return toolResult;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryWorkspaceConsolidate, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryWorkspaceDiscard)]
    [Description("Discards a workspace without promoting anything: removes its outbox context and all its entries.")]
    public async Task<WorkspaceDiscardResult> WorkspaceDiscard(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemoryWorkspaceDiscard);
        activity?.SetTag(ToolActivityTag, TnMemoryWorkspaceDiscard);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryWorkspaceDiscard, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

            var discarded = await workspaces.DiscardAsync(projectId, workspaceId, cancellationToken);
            var result = new WorkspaceDiscardResult(discarded);
            observability.RecordInvocation(TnMemoryWorkspaceDiscard, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemoryWorkspaceDiscard, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemorySweep)]
    [Description(
        "Runs memory degradation: lists (dry_run, default) or deletes entries whose rating is below the threshold and older than their per-entry TTL. Shared entries are never swept.")]
    public async Task<SweepResult> Sweep(
        [Description("The project id.")] string projectId,
        [Description("When true (default), report candidates without deleting.")]
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemorySweep);
        activity?.SetTag(ToolActivityTag, TnMemorySweep);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, dryRun ? AccessRequirement.Read : AccessRequirement.Destructive, TnMemorySweep, cancellationToken);

            var threshold = await knobs.GetSweepThresholdAsync(projectId, cancellationToken);
            var outcome = await sweeper.SweepAsync(projectId, threshold, dryRun, cancellationToken);
            var result = new SweepResult(outcome.Candidates, outcome.DeletedHashes);
            observability.RecordInvocation(TnMemorySweep, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemorySweep, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TnMemorySync)]
    [Description(
        "Syncs the bank's committed contexts (shared + project:<id>) to cloud object storage. " +
        "Configure with `ai-raccoon sync add s3 <url> --bucket <name>` or `ai-raccoon sync add azure " +
        "<container>` (settings table); add `--cli` to use the machine's az/aws CLI login instead of " +
        "stored secrets.")]
    public async Task<SyncToolResult> Sync(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TnMemorySync);
        activity?.SetTag(ToolActivityTag, TnMemorySync);
        activity?.SetTag(ProjectIdActivityTag, projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemorySync, cancellationToken);

            var syncSettings = await syncFactory.ReadOptionsAsync(cancellationToken);
            if (!syncSettings.IsConfigured)
            {
                var notConfigured = new McpException(
                    "sync-not-configured: run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add azure <container>' and enter the credentials when prompted");
                activity?.SetStatus(ActivityStatusCode.Error, notConfigured.Message);
                observability.RecordInvocation(TnMemorySync, sw.Elapsed, true, nameof(McpException));
                throw notConfigured;
            }

            var objectKey = syncSettings.ObjectKey ?? $"memory-{projectId}.db";
            try
            {
                var result = await sync.MemorySyncAsync(projectId, objectKey, cancellationToken);
                var syncResult = new SyncToolResult(result.Sent, result.Received, result.Reindexed);
                observability.RecordInvocation(TnMemorySync, sw.Elapsed, false);
                return syncResult;
            }
            catch (SyncNotConfiguredException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncNotConfiguredException));
                observability.RecordInvocation(TnMemorySync, sw.Elapsed, true, nameof(SyncNotConfiguredException));
                throw new McpException(
                    "sync-not-configured: run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add azure <container>' and enter the credentials when prompted");
            }
            catch (SyncAuthFailedException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncAuthFailedException));
                observability.RecordInvocation(TnMemorySync, sw.Elapsed, true, nameof(SyncAuthFailedException));
                throw new McpException(
                    "sync-auth-failed: run 'az login' (azure --cli) or 'aws configure' / 'aws sso login' (s3 --cli), or verify the keys with 'ai-raccoon sync show'");
            }
            catch (SyncConflictException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncConflictException));
                observability.RecordInvocation(TnMemorySync, sw.Elapsed, true, nameof(SyncConflictException));
                throw new McpException(
                    "sync-conflict: remote changed during merge — retry the sync");
            }
            catch (SyncNetworkException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncNetworkException));
                observability.RecordInvocation(TnMemorySync, sw.Elapsed, true, nameof(SyncNetworkException));
                throw new McpException($"sync-network: {ex.Message}");
            }
            catch (SyncCorruptFileException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncCorruptFileException));
                observability.RecordInvocation(TnMemorySync, sw.Elapsed, true, nameof(SyncCorruptFileException));
                throw new McpException($"sync-corrupt-file: {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is not SyncNotConfiguredException
                                   && ex is not SyncAuthFailedException
                                   && ex is not SyncConflictException
                                   && ex is not SyncNetworkException
                                   && ex is not SyncCorruptFileException
                                   && ex is not McpException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnMemorySync, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WriteResult(string Hash, string Path, string Context, long CreatedAt);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SearchResultList(IReadOnlyList<MemorySearchResult> Results);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ListResult(JsonNode Files);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record StatsResult(int Entries, int Pending, IReadOnlyList<string> Contexts);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ShareResult(bool Shared, string Context);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record DeletedResult(int Deleted);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record DeletedContextResult(int Deleted);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WorkspaceDiscardResult(int Discarded);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record IngestResult(int Indexed);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ScannedResult(int Scanned);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record EmbedResult(int Processed, int Pending);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WorkspaceBeginResult(string WorkspaceId, string Context);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WorkspaceStatusResult(IReadOnlyList<MemoryEntry> Entries, int Count);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ConsolidationToolResult(int Promoted, int Discarded);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SweepResult(IReadOnlyList<SweepCandidate> Candidates, IReadOnlyList<string> Deleted);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SyncToolResult(int Sent, int Received, int Reindexed);

    private sealed record ToolExecutionActivity : IDisposable
    {
        private const string ToolActivityTag = "tool";
        private const string ProjectIdActivityTag = "project_id";

        private readonly Activity? _activity;
        private readonly ToolCallMetrics _metrics;
        private readonly Stopwatch _stopwatch;
        private readonly string _toolName;

        public ToolExecutionActivity(ToolCallMetrics metrics, string toolName, string projectId)
        {
            _metrics = metrics;
            _toolName = toolName;
            _activity = metrics.ActivitySource.StartActivity(toolName);
            _activity?.SetTag(ToolActivityTag, toolName);
            _activity?.SetTag(ProjectIdActivityTag, projectId);
            _stopwatch = Stopwatch.StartNew();
        }

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void Dispose()
        {
            _activity?.Dispose();
            _stopwatch.Stop();
            _stopwatch.Reset();
        }

        public void RecordInvocation() => _metrics.RecordInvocation(_toolName, _stopwatch.Elapsed, false);

        public void RecordError(Exception exception)
        {
            _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            _metrics.RecordInvocation(_toolName, _stopwatch.Elapsed, true, exception.GetType().Name);
        }
    }
}
