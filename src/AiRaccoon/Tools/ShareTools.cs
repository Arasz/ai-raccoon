using System.ComponentModel;
using System.Runtime.InteropServices;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over the shared-extraction pipeline — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class ShareTools(
    IMemoryStore store,
    IToolGate gate,
    IShareExtractService shareExtract)
{
    private const string TnMemoryShare = "memory_share";
    private const string TnMemoryShareExtract = "memory_share_extract";

    [McpServerTool(Name = TnMemoryShare)]
    [Description(
        "Promotes an existing project entry into the flat shared context — the curated, cross-project, sweep-exempt tier. Nothing is shared without this explicit promotion.")]
    public async Task<ApiEnvelope<ShareResult>> Share(
        [Description("The project id.")] [Optional][DefaultParameterValue("")] string projectId,
        [Description("The content hash to promote.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemoryShare, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var entry = await store.ShareAsync(canonical, hash, cancellationToken);
        var result = new ShareResult(true, entry.Entry.Context);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemoryShareExtract)]
    [Description(
        "Checks committed project memories and extracts the ones worth sharing. propose (default) ranks candidates and PERSISTS them into the propose tier — the waiting queue the agent reviews with memory_promotion_list. promote shares the top queued candidates into the curated, sweep-exempt shared tier and drains them. autoPromote (disabled by default) promotes the top queued candidates in the same call — it shares data BETWEEN PROJECTS, so it requires confirm=true as an explicit enable gate. Sharing stays explicit: propose first, review, then promote.")]
    public async Task<ApiEnvelope<ShareExtractResult>> ShareExtract(
        [Description("Project ids to scan (1..8).")]
        string[] projectIds,
        [Description("propose (default) queues ranked candidates; promote shares the top queued candidates.")]
        string mode = "propose",
        [Description("Maximum candidates to queue or promote per project (1..50; default 20).")]
        int? limit = null,
        [Description("Include rows with a TTL (ephemeral by design; promoting makes them sweep-exempt forever).")]
        bool includeTtlRows = false,
        [Description("Promote the top queued candidates in this call. Disabled by default: it shares data between projects.")]
        bool autoPromote = false,
        [Description("Explicit enable gate for autoPromote — acknowledges that candidates become visible to every project.")]
        bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var request = new ShareExtractRequest(projectIds ?? [], mode, limit, includeTtlRows, autoPromote, confirm);
        var canonicalIds = new List<string>(request.ProjectIds.Count);
        foreach (var projectId in request.ProjectIds)
        {
            canonicalIds.Add(await gate.RequireAsync(projectId,
                    request.Promotes ? AccessRequirement.Write : AccessRequirement.Read,
                    TnMemoryShareExtract, cancellationToken)
                .ConfigureAwait(false));
        }

        request = request with { ProjectIds = [.. canonicalIds] };
        var result = await shareExtract.RunAsync(request, cancellationToken).ConfigureAwait(false);
        return await gate.WrapAsync(request.MetaProjectId, result, cancellationToken);
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ShareResult(bool Shared, string Context);
}
