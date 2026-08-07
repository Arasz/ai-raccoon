using System.ComponentModel;
using System.Text.Json.Nodes;
using AiRaccoon.Access;
using AiRaccoon.Core;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Observability;
using FluentValidation;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IMemoryStore — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class MemoryTools(
    IMemoryStore store,
    IMemoryAccessGuard access,
    ToolCallMetrics observability,
    IPromotionQueue queue)
{
    private const string TnMemoryWrite = "memory_write";
    private const string TnMemorySearch = "memory_search";
    private const string TnMemoryList = "memory_list";
    private const string TnMemoryStats = "memory_stats";
    private const string TnMemoryDelete = "memory_delete";
    private const string TnMemoryDeleteContext = "memory_delete_context";
    private const string TnMemoryIngestFile = "memory_ingest_file";
    private const string TnMemoryIngestDirectory = "memory_ingest_directory";
    private const string TnMemoryEmbedPending = "memory_embed_pending";

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
    public async Task<ApiEnvelope<WriteResult>> Write(
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
        using var activity = new ToolExecutionActivity(observability, TnMemoryWrite, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryWrite, cancellationToken);

            var request = new MemoryWriteRequest(projectId, content, context, agentId, workspaceId, sourceFile, section);
            await MemoryWriteRequestValidator.ValidateAndThrowAsync(request, cancellationToken);

            var entry = await store.WriteAsync(request, cancellationToken);
            var result = new WriteResult(entry.Hash, entry.Path, entry.Context, entry.CreatedAt);
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemorySearch)]
    [Description(
        "Hybrid semantic search over the bank. scope=all (default) searches shared + project (+ workspace when named); scope=project searches the project only; scope=shared searches the shared promotion tier only.")]
    public async Task<ApiEnvelope<SearchResultList>> Search(
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
        using var activity = new ToolExecutionActivity(observability, TnMemorySearch, projectId);
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
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryList)]
    [Description("Lists the bank's indexed files as a JSON tree (memory_list_files).")]
    public async Task<ApiEnvelope<ListResult>> List(
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
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryStats)]
    [Description("Reports entry count, pending (deferred-embedding) count, and the bank's committed contexts.")]
    public async Task<ApiEnvelope<StatsResult>> Stats(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryStats, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TnMemoryStats, cancellationToken);
            var stats = await store.GetStatsAsync(projectId, cancellationToken);
            var result = new StatsResult(stats.EntryCount, stats.PendingCount, stats.Contexts);
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryDelete)]
    [Description("Deletes a specific memory entry by its content hash.")]
    public async Task<ApiEnvelope<DeletedResult>> Delete(
        [Description("The project id.")] string projectId,
        [Description("The content hash to delete.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryDelete, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryDelete, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);

            var deleted = await store.DeleteAsync(projectId, hash, cancellationToken);
            var result = new DeletedResult(deleted ? 1 : 0);
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryDeleteContext)]
    [Description("Deletes every entry stored under a context label (e.g. a project or workspace context).")]
    public async Task<ApiEnvelope<DeletedContextResult>> DeleteContext(
        [Description("The project id.")] string projectId,
        [Description("The context label to delete.")]
        string context,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryDeleteContext, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Destructive, TnMemoryDeleteContext, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(context);

            var deleted = await store.DeleteContextAsync(projectId, context, cancellationToken);
            var result = new DeletedContextResult(deleted);
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryIngestFile)]
    [Description("Indexes one file from disk into memory.")]
    public async Task<ApiEnvelope<IngestResult>> IngestFile(
        [Description("The project id.")] string projectId,
        [Description("Path of the file to index.")]
        string path,
        [Description("Optional context label.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryIngestFile, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryIngestFile, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var indexed = await store.IngestFileAsync(projectId, path, context, cancellationToken);
            var result = new IngestResult(indexed);
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryIngestDirectory)]
    [Description("Recursively indexes a directory tree into memory, skipping unchanged files.")]
    public async Task<ApiEnvelope<ScannedResult>> IngestDirectory(
        [Description("The project id.")] string projectId,
        [Description("Path of the directory to index.")]
        string path,
        [Description("Optional context label applied to all files.")]
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryIngestDirectory, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryIngestDirectory, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var scanned = await store.IngestDirectoryAsync(projectId, path, context, cancellationToken);
            var result = new ScannedResult(scanned);
            var envelope = await WrapAsync(result, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryEmbedPending)]
    [Description("Embeds deferred entries in batches (used when no model was configured at write time).")]
    public async Task<ApiEnvelope<EmbedResult>> EmbedPending(
        [Description("The project id.")] string projectId,
        [Description("Maximum rows to process in this call; omit for all.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryEmbedPending, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryEmbedPending, cancellationToken);

            var result = await store.EmbedPendingAsync(projectId, limit, cancellationToken);
            var embedResult = new EmbedResult(result.Processed, result.Pending);
            var envelope = await WrapAsync(embedResult, cancellationToken);
            activity.RecordInvocation();
            return envelope;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    private async Task<ApiEnvelope<T>> WrapAsync<T>(T data, CancellationToken cancellationToken) =>
        new(data, await queue.GetMetaAsync(cancellationToken).ConfigureAwait(false));

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record WriteResult(string Hash, string Path, string Context, long CreatedAt);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SearchResultList(IReadOnlyList<MemorySearchResult> Results);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ListResult(JsonNode Files);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record StatsResult(int Entries, int Pending, IReadOnlyList<string> Contexts);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record DeletedResult(int Deleted);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record DeletedContextResult(int Deleted);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record IngestResult(int Indexed);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ScannedResult(int Scanned);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record EmbedResult(int Processed, int Pending);
}
