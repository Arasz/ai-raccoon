using System.ComponentModel;
using System.Runtime.InteropServices;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.SearchQuality;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over ISearchQualityService — no business logic here.</summary>
public sealed class QualityTools(
    ISearchQualityService qualityService,
    IToolGate gate)
{
    private const string TnRecordFollowThrough = "memory_record_followthrough";
    private const string TnRecordGrade = "memory_record_grade";

    [McpServerTool(Name = TnRecordFollowThrough)]
    [Description(
        "Records that the agent read a file that appeared in a prior memory_search result. " +
        "Updates the existing quality record keyed by correlationId. " +
        "Call this when the agent opens a file that was returned by memory_search. " +
        "servedRank is the 1-based rank the file was served at, when known; rank-only telemetry " +
        "is section-ambiguous under kind=both by design.")]
    public async Task<ApiEnvelope<FollowThroughResult>> RecordFollowThrough(
        [Description("The project id.")] [Optional][DefaultParameterValue("")] string projectId,
        [Description("The correlationId returned by the preceding memory_search call.")]
        string correlationId,
        [Description("Absolute path of the file the agent read.")]
        string filePath,
        [Description("1-based rank the file was served at, when known.")]
        int? servedRank = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnRecordFollowThrough, cancellationToken);

        await qualityService.RecordFollowThroughAsync(correlationId, filePath, servedRank, cancellationToken);
        var envelope = await gate.WrapAsync(canonical, new FollowThroughResult(true), cancellationToken);

        return envelope;
    }

    [McpServerTool(Name = TnRecordGrade)]
    [Description(
        "Records a human usefulness grade (1-5) for a prior memory_search call. " +
        "Updates the existing quality record keyed by correlationId.")]
    public async Task<ApiEnvelope<GradeResult>> RecordGrade(
        [Description("The project id.")] [Optional][DefaultParameterValue("")] string projectId,
        [Description("The correlationId returned by the preceding memory_search call.")]
        string correlationId,
        [Description("Usefulness grade 1-5 (5=best).")]
        int grade,
        [Description("Optional note explaining the grade.")]
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnRecordGrade, cancellationToken);

        await qualityService.RecordGradeAsync(canonical, correlationId, grade, note, cancellationToken);
        var envelope = await gate.WrapAsync(canonical, new GradeResult(true), cancellationToken);

        return envelope;
    }

    public sealed record FollowThroughResult(bool Recorded);

    public sealed record GradeResult(bool Recorded);
}
