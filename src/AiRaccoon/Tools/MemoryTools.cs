using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using FluentValidation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IMemoryStore and the workspace/sweep/sync services — no business logic here (spec §6.1).</summary>
public sealed class MemoryTools(
    IMemoryStore store,
    SyncService sync,
    WorkspaceService workspaces,
    SweepService sweeper,
    IMemoryAccessGuard access,
    SyncOptions syncOptions,
    ForgettingPolicyService knobs,
    ToolCallMetrics observability)
{
    // ── MCP tool name constants ──
    private const string TN_MEMORY_WRITE = "memory_write";
    private const string TN_MEMORY_SEARCH = "memory_search";
    private const string TN_MEMORY_LIST = "memory_list";
    private const string TN_MEMORY_STATS = "memory_stats";
    private const string TN_MEMORY_SHARE = "memory_share";
    private const string TN_MEMORY_DELETE = "memory_delete";
    private const string TN_MEMORY_DELETE_CONTEXT = "memory_delete_context";
    private const string TN_MEMORY_INGEST_FILE = "memory_ingest_file";
    private const string TN_MEMORY_INGEST_DIRECTORY = "memory_ingest_directory";
    private const string TN_MEMORY_CONFIGURE = "memory_configure";
    private const string TN_MEMORY_EMBED_PENDING = "memory_embed_pending";
    private const string TN_MEMORY_SET_STRUCTURE_ALPHA = "memory_set_structure_alpha";
    private const string TN_MEMORY_WORKSPACE_BEGIN = "memory_workspace_begin";
    private const string TN_MEMORY_WORKSPACE_STATUS = "memory_workspace_status";
    private const string TN_MEMORY_WORKSPACE_CONSOLIDATE = "memory_workspace_consolidate";
    private const string TN_MEMORY_WORKSPACE_DISCARD = "memory_workspace_discard";
    private const string TN_MEMORY_SWEEP = "memory_sweep";
    private const string TN_MEMORY_SYNC = "memory_sync";

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

    [McpServerTool(Name = TN_MEMORY_WRITE)]
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
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_WRITE);
        activity?.SetTag("tool", TN_MEMORY_WRITE);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_WRITE, cancellationToken);

            var request = new MemoryWriteRequest(projectId, content, context, agentId, workspaceId, sourceFile, section);
            new MemoryWriteRequest.Validator().ValidateAndThrow(request);

            var entry = await store.WriteAsync(request, cancellationToken);
            var result = new WriteResult(entry.Hash, entry.Path, entry.Context, entry.CreatedAt);
            observability.RecordInvocation(TN_MEMORY_WRITE, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_WRITE, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_SEARCH)]
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
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_SEARCH);
        activity?.SetTag("tool", TN_MEMORY_SEARCH);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TN_MEMORY_SEARCH, cancellationToken);

            var parsedScope = scope.ToLowerInvariant() switch
            {
                "all" => SearchScope.All,
                "project" => SearchScope.Project,
                "shared" => SearchScope.Shared,
                _ => throw new McpException($"Invalid scope '{scope}': expected all, project, or shared.")
            };

            var searchQuery = new SearchQuery(projectId, query, parsedScope, workspaceId, limit, minScore,
                rrfK, ftsWeight, vectorWeight, contextLabel);
            new SearchQuery.Validator().ValidateAndThrow(searchQuery);

            var results = await store.SearchAsync(searchQuery, cancellationToken);
            var result = new SearchResultList(results);
            observability.RecordInvocation(TN_MEMORY_SEARCH, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_SEARCH, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_LIST)]
    [Description("Lists the bank's indexed files as a JSON tree (memory_list_files).")]
    public async Task<ListResult> List(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_LIST);
        activity?.SetTag("tool", TN_MEMORY_LIST);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TN_MEMORY_LIST, cancellationToken);
            var files = await store.ListFilesAsync(projectId, cancellationToken);
            var result = new ListResult(files);
            observability.RecordInvocation(TN_MEMORY_LIST, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_LIST, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_STATS)]
    [Description("Reports entry count, pending (deferred-embedding) count, and the bank's committed contexts.")]
    public async Task<StatsResult> Stats(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_STATS);
        activity?.SetTag("tool", TN_MEMORY_STATS);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TN_MEMORY_STATS, cancellationToken);
            var stats = await store.GetStatsAsync(projectId, cancellationToken);
            var result = new StatsResult(stats.EntryCount, stats.PendingCount, stats.Contexts);
            observability.RecordInvocation(TN_MEMORY_STATS, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_STATS, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_SHARE)]
    [Description(
        "Promotes an existing project entry into the flat shared context — the curated, cross-project, sweep-exempt tier. Nothing is shared without this explicit promotion.")]
    public async Task<ShareResult> Share(
        [Description("The project id.")] string projectId,
        [Description("The content hash to promote.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_SHARE);
        activity?.SetTag("tool", TN_MEMORY_SHARE);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_SHARE, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);

            var entry = await store.ShareAsync(projectId, hash, cancellationToken);
            var result = new ShareResult(true, entry.Context);
            observability.RecordInvocation(TN_MEMORY_SHARE, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_SHARE, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_DELETE)]
    [Description("Deletes a specific memory entry by its content hash.")]
    public async Task<DeletedResult> Delete(
        [Description("The project id.")] string projectId,
        [Description("The content hash to delete.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_DELETE);
        activity?.SetTag("tool", TN_MEMORY_DELETE);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TN_MEMORY_DELETE, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);

            var deleted = await store.DeleteAsync(projectId, hash, cancellationToken);
            var result = new DeletedResult(deleted ? 1 : 0);
            observability.RecordInvocation(TN_MEMORY_DELETE, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_DELETE, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_DELETE_CONTEXT)]
    [Description("Deletes every entry stored under a context label (e.g. a project or workspace context).")]
    public async Task<DeletedContextResult> DeleteContext(
        [Description("The project id.")] string projectId,
        [Description("The context label to delete.")]
        string context,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_DELETE_CONTEXT);
        activity?.SetTag("tool", TN_MEMORY_DELETE_CONTEXT);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TN_MEMORY_DELETE_CONTEXT, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(context);

            var deleted = await store.DeleteContextAsync(projectId, context, cancellationToken);
            var result = new DeletedContextResult(deleted);
            observability.RecordInvocation(TN_MEMORY_DELETE_CONTEXT, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_DELETE_CONTEXT, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_INGEST_FILE)]
    [Description("Indexes one file from disk into memory.")]
    public async Task<IngestResult> IngestFile(
        [Description("The project id.")] string projectId,
        [Description("Path of the file to index.")]
        string path,
        [Description("Optional context label.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_INGEST_FILE);
        activity?.SetTag("tool", TN_MEMORY_INGEST_FILE);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_INGEST_FILE, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var indexed = await store.IngestFileAsync(projectId, path, context, cancellationToken);
            var result = new IngestResult(indexed);
            observability.RecordInvocation(TN_MEMORY_INGEST_FILE, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_INGEST_FILE, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_INGEST_DIRECTORY)]
    [Description("Recursively indexes a directory tree into memory, skipping unchanged files.")]
    public async Task<ScannedResult> IngestDirectory(
        [Description("The project id.")] string projectId,
        [Description("Path of the directory to index.")]
        string path,
        [Description("Optional context label applied to all files.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_INGEST_DIRECTORY);
        activity?.SetTag("tool", TN_MEMORY_INGEST_DIRECTORY);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_INGEST_DIRECTORY, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var scanned = await store.IngestDirectoryAsync(projectId, path, context, cancellationToken);
            var result = new ScannedResult(scanned);
            observability.RecordInvocation(TN_MEMORY_INGEST_DIRECTORY, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_INGEST_DIRECTORY, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_CONFIGURE)]
    [Description(
        "Configures the bank's embedding engine. provider 'local' embeds in-process with the bundled " +
        "int8 ONNX model (optional model path overrides it); provider 'openai' routes through any " +
        "OpenAI-compatible baseUrl (default https://api.openai.com/v1) with a model id. Remote requires " +
        "an API key (env AIRACCOON_OPENAI_API_KEY or api_key). Changing the engine re-embeds the bank.")]
    public async Task<ConfigureResult> Configure(
        [Description("The project id; every memory operation is scoped to a project.")]
        string projectId,
        [Description("Embedding provider: 'local' (bundled ONNX) or 'openai' (OpenAI-compatible endpoint).")]
        string provider,
        [Description("Endpoint base URL for provider 'openai' (e.g. http://localhost:11434/v1); defaults to the OpenAI API.")]
        string? baseUrl = null,
        [Description("Model id (openai) or ONNX model path (local); defaults to the bundled model for local.")]
        string? model = null,
        [Description("Optional API key for remote embeddings; never persisted.")]
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_CONFIGURE);
        activity?.SetTag("tool", TN_MEMORY_CONFIGURE);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_CONFIGURE, cancellationToken);

            var isLocal = string.Equals(provider, "local", StringComparison.OrdinalIgnoreCase);
            var isOpenAi = string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase);
            if (!isLocal && !isOpenAi)
            {
                throw new McpException($"invalid-params: provider must be 'local' or 'openai', got '{provider}'");
            }

            var resolvedKey = apiKey;
            if (isOpenAi)
            {
                if (string.IsNullOrWhiteSpace(model))
                {
                    throw new McpException("invalid-params: model is required for provider 'openai'");
                }

                resolvedKey = apiKey ?? Environment.GetEnvironmentVariable(
                    AiRaccoon.Infrastructure.Embedding.EmbeddingService.OpenAiApiKeyEnvVar);
                if (string.IsNullOrWhiteSpace(resolvedKey))
                {
                    throw new McpException(
                        "embedding-api-key-missing: set AIRACCOON_OPENAI_API_KEY or pass api_key for provider 'openai'");
                }
            }

            var config = await store.ConfigureEmbeddingAsync(projectId, provider, model, baseUrl, resolvedKey,
                cancellationToken);
            var result = new ConfigureResult(config.Provider, config.Model, config.Engine);
            observability.RecordInvocation(TN_MEMORY_CONFIGURE, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_CONFIGURE, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_SET_STRUCTURE_ALPHA)]
    [Description(
        "Sets the dual-vector fusion alpha (retrieval.structureAlpha, 0..1) for the bank: " +
        "score = alpha * content similarity + (1 - alpha) * heading-path structure similarity. " +
        "Default 0.5; higher favors content, lower favors structure. Applies to subsequent searches.")]
    public async Task<SettingResult> SetStructureAlpha(
        [Description("The project id; every memory operation is scoped to a project.")]
        string projectId,
        [Description("Fusion alpha in [0, 1].")]
        double alpha,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_SET_STRUCTURE_ALPHA);
        activity?.SetTag("tool", TN_MEMORY_SET_STRUCTURE_ALPHA);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_SET_STRUCTURE_ALPHA, cancellationToken);

            if (alpha is < 0.0 or > 1.0)
            {
                throw new McpException($"invalid-params: alpha must be in [0, 1], got {alpha}");
            }

            var value = alpha.ToString(CultureInfo.InvariantCulture);
            await store.SetSettingAsync(StructureFusion.AlphaSettingKey, value, cancellationToken);
            observability.RecordInvocation(TN_MEMORY_SET_STRUCTURE_ALPHA, sw.Elapsed, false);
            return new SettingResult(StructureFusion.AlphaSettingKey, value);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_SET_STRUCTURE_ALPHA, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_EMBED_PENDING)]
    [Description("Embeds deferred entries in batches (used when no model was configured at write time).")]
    public async Task<EmbedResult> EmbedPending(
        [Description("The project id.")] string projectId,
        [Description("Maximum rows to process in this call; omit for all.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_EMBED_PENDING);
        activity?.SetTag("tool", TN_MEMORY_EMBED_PENDING);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_EMBED_PENDING, cancellationToken);

            var result = await store.EmbedPendingAsync(projectId, limit, cancellationToken);
            var embedResult = new EmbedResult(result.Processed, result.Pending);
            observability.RecordInvocation(TN_MEMORY_EMBED_PENDING, sw.Elapsed, false);
            return embedResult;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_EMBED_PENDING, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_WORKSPACE_BEGIN)]
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
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_WORKSPACE_BEGIN);
        activity?.SetTag("tool", TN_MEMORY_WORKSPACE_BEGIN);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_WORKSPACE_BEGIN, cancellationToken);

            var workspace = await workspaces.BeginAsync(projectId, cancellationToken);
            var result = new WorkspaceBeginResult(workspace.Id, workspace.Context);
            observability.RecordInvocation(TN_MEMORY_WORKSPACE_BEGIN, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_WORKSPACE_BEGIN, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_WORKSPACE_STATUS)]
    [Description("Lists the entries currently in a workspace's outbox.")]
    public async Task<WorkspaceStatusResult> WorkspaceStatus(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_WORKSPACE_STATUS);
        activity?.SetTag("tool", TN_MEMORY_WORKSPACE_STATUS);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TN_MEMORY_WORKSPACE_STATUS, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

            var entries = await workspaces.GetStatusAsync(projectId, workspaceId, cancellationToken);
            var result = new WorkspaceStatusResult(entries, entries.Count);
            observability.RecordInvocation(TN_MEMORY_WORKSPACE_STATUS, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_WORKSPACE_STATUS, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_WORKSPACE_CONSOLIDATE)]
    [Description(
        "Finishes a workspace: promotes the kept hashes (or 'all') from the workspace outbox into the project's committed memory, then removes the workspace context.")]
    public async Task<ConsolidationToolResult> WorkspaceConsolidate(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        [Description("Hashes to promote, or ['all'] to promote everything.")]
        string[] keep,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_WORKSPACE_CONSOLIDATE);
        activity?.SetTag("tool", TN_MEMORY_WORKSPACE_CONSOLIDATE);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TN_MEMORY_WORKSPACE_CONSOLIDATE, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
            ArgumentNullException.ThrowIfNull(keep);

            var result = await workspaces.ConsolidateAsync(projectId, workspaceId, keep, cancellationToken);
            var toolResult = new ConsolidationToolResult(result.Promoted, result.Discarded);
            observability.RecordInvocation(TN_MEMORY_WORKSPACE_CONSOLIDATE, sw.Elapsed, false);
            return toolResult;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_WORKSPACE_CONSOLIDATE, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_WORKSPACE_DISCARD)]
    [Description("Discards a workspace without promoting anything: removes its outbox context and all its entries.")]
    public async Task<DeletedContextResult> WorkspaceDiscard(
        [Description("The project id.")] string projectId,
        [Description("The workspace id.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_WORKSPACE_DISCARD);
        activity?.SetTag("tool", TN_MEMORY_WORKSPACE_DISCARD);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TN_MEMORY_WORKSPACE_DISCARD, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

            var discarded = await workspaces.DiscardAsync(projectId, workspaceId, cancellationToken);
            var result = new DeletedContextResult(discarded);
            observability.RecordInvocation(TN_MEMORY_WORKSPACE_DISCARD, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_WORKSPACE_DISCARD, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_SWEEP)]
    [Description(
        "Runs memory degradation: lists (dry_run, default) or deletes entries whose rating is below the threshold and older than the TTL. Shared entries are never swept.")]
    public async Task<SweepResult> Sweep(
        [Description("The project id.")] string projectId,
        [Description("When true (default), report candidates without deleting.")]
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_SWEEP);
        activity?.SetTag("tool", TN_MEMORY_SWEEP);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, dryRun ? AccessRequirement.Read : AccessRequirement.Destructive, TN_MEMORY_SWEEP, cancellationToken);

            var threshold = await knobs.GetSweepThresholdAsync(projectId, cancellationToken);
            var ttlDays = await knobs.GetSweepTtlDaysAsync(projectId, cancellationToken);
            var outcome = await sweeper.SweepAsync(projectId, threshold, ttlDays, dryRun, cancellationToken);
            var result = new SweepResult(outcome.Candidates, outcome.DeletedHashes);
            observability.RecordInvocation(TN_MEMORY_SWEEP, sw.Elapsed, false);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_MEMORY_SWEEP, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_MEMORY_SYNC)]
    [Description(
        "Syncs the bank's committed contexts (shared + project:<id>) to S3-compatible object storage. " +
        "Requires AIRACCOON_SYNC_ENDPOINT, AIRACCOON_SYNC_BUCKET, AIRACCOON_SYNC_ACCESS_KEY " +
        "and AIRACCOON_SYNC_SECRET_KEY.")]
    public async Task<SyncToolResult> Sync(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_MEMORY_SYNC);
        activity?.SetTag("tool", TN_MEMORY_SYNC);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_MEMORY_SYNC, cancellationToken);

            if (!syncOptions.IsConfigured)
            {
                var notConfigured = new McpException(
                    "sync-not-configured: set AIRACCOON_SYNC_ENDPOINT, AIRACCOON_SYNC_BUCKET, " +
                    "AIRACCOON_SYNC_ACCESS_KEY and AIRACCOON_SYNC_SECRET_KEY");
                activity?.SetStatus(ActivityStatusCode.Error, notConfigured.Message);
                observability.RecordInvocation(TN_MEMORY_SYNC, sw.Elapsed, true, nameof(McpException));
                throw notConfigured;
            }

            var objectKey = syncOptions.ObjectKey ?? $"memory-{projectId}.db";
            try
            {
                var result = await sync.MemorySyncAsync(projectId, objectKey, cancellationToken);
                var syncResult = new SyncToolResult(result.Sent, result.Received, result.Reindexed);
                observability.RecordInvocation(TN_MEMORY_SYNC, sw.Elapsed, false);
                return syncResult;
            }
            catch (SyncNotConfiguredException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncNotConfiguredException));
                observability.RecordInvocation(TN_MEMORY_SYNC, sw.Elapsed, true, nameof(SyncNotConfiguredException));
                throw new McpException(
                    "sync-not-configured: set AIRACCOON_SYNC_ENDPOINT, AIRACCOON_SYNC_BUCKET, " +
                    "AIRACCOON_SYNC_ACCESS_KEY and AIRACCOON_SYNC_SECRET_KEY");
            }
            catch (SyncAuthFailedException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncAuthFailedException));
                observability.RecordInvocation(TN_MEMORY_SYNC, sw.Elapsed, true, nameof(SyncAuthFailedException));
                throw new McpException(
                    "sync-auth-failed: verify AIRACCOON_SYNC_ACCESS_KEY and AIRACCOON_SYNC_SECRET_KEY");
            }
            catch (SyncConflictException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncConflictException));
                observability.RecordInvocation(TN_MEMORY_SYNC, sw.Elapsed, true, nameof(SyncConflictException));
                throw new McpException(
                    "sync-conflict: remote changed during merge — retry the sync");
            }
            catch (SyncNetworkException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncNetworkException));
                observability.RecordInvocation(TN_MEMORY_SYNC, sw.Elapsed, true, nameof(SyncNetworkException));
                throw new McpException($"sync-network: {ex.Message}");
            }
            catch (SyncCorruptFileException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(SyncCorruptFileException));
                observability.RecordInvocation(TN_MEMORY_SYNC, sw.Elapsed, true, nameof(SyncCorruptFileException));
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
            observability.RecordInvocation(TN_MEMORY_SYNC, sw.Elapsed, true, ex.GetType().Name);
            throw;
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
