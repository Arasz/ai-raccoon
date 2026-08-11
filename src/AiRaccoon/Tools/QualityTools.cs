using System.ComponentModel;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.SearchQuality;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over ISearchQualityService — no business logic here.</summary>
public sealed class QualityTools(
    ISearchQualityService qualityService,
    ToolGate gate)
{
    private const string TnRecordFollowThrough = "memory_record_followthrough";

    [McpServerTool(Name = TnRecordFollowThrough)]
    [Description(
        "Records that the agent read a file that appeared in a prior memory_search result. " +
        "Updates the existing quality record keyed by correlationId. " +
        "Call this when the agent opens a file that was returned by memory_search.")]
    public async Task<ApiEnvelope<FollowThroughResult>> RecordFollowThrough(
        [Description("The project id.")] string projectId,
        [Description("The correlationId returned by the preceding memory_search call.")] string correlationId,
        [Description("Absolute path of the file the agent read.")] string filePath,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Write, TnRecordFollowThrough, cancellationToken);

        await qualityService.RecordFollowThroughAsync(correlationId, filePath, cancellationToken);
        var envelope = await gate.WrapAsync(projectId, new FollowThroughResult(true), cancellationToken);

        return envelope;
    }

    public sealed record FollowThroughResult(bool Recorded);
}
