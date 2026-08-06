using System.ComponentModel;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Observability;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over the shared-extraction pipeline — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class ShareTools(
    IMemoryStore store,
    IMemoryAccessGuard access,
    ToolCallMetrics observability,
    SharedExtractionService extraction)
{
    private const string TnMemoryShare = "memory_share";
    private const string TnMemoryShareExtract = "memory_share_extract";

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

    [McpServerTool(Name = TnMemoryShare)]
    [Description(
        "Promotes an existing project entry into the flat shared context — the curated, cross-project, sweep-exempt tier. Nothing is shared without this explicit promotion.")]
    public async Task<ShareResult> Share(
        [Description("The project id.")] string projectId,
        [Description("The content hash to promote.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryShare, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnMemoryShare, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);

            var entry = await store.ShareAsync(projectId, hash, cancellationToken);
            var result = new ShareResult(true, entry.Context);
            activity.RecordInvocation();
            return result;
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnMemoryShareExtract)]
    [Description(
        "Checks committed project memories and extracts the ones worth sharing. propose (default) returns ranked candidates with the reasons they scored; promote shares the top candidates into the curated, sweep-exempt shared tier. autoPromote (disabled by default) promotes the top candidates in the same call — it shares data BETWEEN PROJECTS, so it requires confirm=true as an explicit enable gate. Sharing stays explicit: propose first, review, then promote.")]
    public async Task<ShareExtractResult> ShareExtract(
        [Description("Project ids to scan (1..8).")]
        string[] projectIds,
        [Description("propose (default) lists ranked candidates; promote shares the top candidates.")]
        string mode = "propose",
        [Description("Maximum candidates to return or promote (1..50; default 20).")]
        int? limit = null,
        [Description("Include rows with a TTL (ephemeral by design; promoting makes them sweep-exempt forever).")]
        bool includeTtlRows = false,
        [Description("Promote the top candidates in this call. Disabled by default: it shares data between projects.")]
        bool autoPromote = false,
        [Description("Explicit enable gate for autoPromote — acknowledges that candidates become visible to every project.")]
        bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemoryShareExtract,
            string.Join(",", projectIds ?? []));
        try
        {
            if (projectIds is null || projectIds.Length == 0 || projectIds.Length > 8)
            {
                throw new McpException("invalid-params: projectIds must contain 1..8 project ids");
            }

            var extractMode = mode switch
            {
                "propose" => ExtractMode.Propose,
                "promote" => ExtractMode.Promote,
                _ => throw new McpException("invalid-params: mode must be 'propose' or 'promote'")
            };
            var resolvedLimit = limit ?? SharedExtractionService.DefaultCandidateLimit;
            if (resolvedLimit is < 1 or > 50)
            {
                throw new McpException("invalid-params: limit must be between 1 and 50");
            }

            if (autoPromote && !confirm)
            {
                throw new McpException(
                    "confirm-required: autoPromote shares candidates with ALL projects — pass confirm=true to enable");
            }

            var promotes = extractMode == ExtractMode.Promote || autoPromote;
            foreach (var projectId in projectIds)
            {
                RequireProjectId(projectId);
                await RequireAsync(projectId,
                        promotes ? AccessRequirement.Write : AccessRequirement.Read,
                        TnMemoryShareExtract, cancellationToken)
                    .ConfigureAwait(false);
            }

            var sharedIndex = await store.GetSharedIndexAsync(cancellationToken).ConfigureAwait(false);
            var candidates = new List<ShareCandidate>();
            var promoted = new List<string>();
            foreach (var projectId in projectIds)
            {
                var rows = await store.ExtractCandidatesAsync(projectId, includeTtlRows, cancellationToken)
                    .ConfigureAwait(false);
                var result = extraction.Run(promotes ? ExtractMode.Promote : ExtractMode.Propose,
                    projectId, projectIds, rows,
                    sharedIndex.Values, sharedIndex.Paths, includeTtlRows, resolvedLimit,
                    DateTimeOffset.UtcNow);
                candidates.AddRange(result.Candidates);
                foreach (var hash in result.PromotedHashes)
                {
                    await store.ShareAsync(projectId, hash, cancellationToken).ConfigureAwait(false);
                    promoted.Add(hash);
                }
            }

            activity.RecordInvocation();
            return new ShareExtractResult(candidates, promoted);
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ShareResult(bool Shared, string Context);
}
