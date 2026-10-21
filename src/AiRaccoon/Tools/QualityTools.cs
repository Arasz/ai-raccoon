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
    private const string TnGetQualityReport = "get_quality_report";
    private const string TnSetMemoryUsageRecord = "set_memory_usage_record";
    private const string TnSetQueryResultGrade = "set_query_result_grade";

    [McpServerTool(Name = TnSetMemoryUsageRecord)]
    [McpToolScope(ToolScope.All)]
    [Description(
        "Records that the agent read a file that appeared in a prior memory_search result. " +
        "Updates the existing quality record keyed by correlationId; the record must belong to this project, " +
        "and recorded is false when none does. " +
        "Call this when the agent opens a file that was returned by memory_search. " +
        "servedRank is the 1-based rank the file was served at, when known; rank-only telemetry " +
        "is section-ambiguous under kind=both by design.")]
    public async Task<ApiEnvelope<FollowThroughResult>> RecordFollowThrough(
        [Description("The project id.")][Optional][DefaultParameterValue("")] string projectId,
        [Description("The correlationId returned by the preceding memory_search call.")]
        string correlationId,
        [Description("Absolute path of the file the agent read.")]
        string filePath,
        [Description("1-based rank the file was served at, when known.")]
        int? servedRank = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnSetMemoryUsageRecord, cancellationToken);

        var recorded = await qualityService.RecordFollowThroughAsync(canonical, correlationId, filePath, servedRank, cancellationToken);
        var envelope = await gate.WrapAsync(canonical, new FollowThroughResult(recorded), cancellationToken);

        return envelope;
    }

    [McpServerTool(Name = TnSetQueryResultGrade)]
    [McpToolScope(ToolScope.All)]
    [Description(
        "Records a human usefulness grade (1-5) for a prior memory_search call. " +
        "Updates the existing quality record keyed by correlationId; the record must belong to this project, " +
        "and recorded is false when none does.")]
    public async Task<ApiEnvelope<GradeResult>> RecordGrade(
        [Description("The project id.")][Optional][DefaultParameterValue("")] string projectId,
        [Description("The correlationId returned by the preceding memory_search call.")]
        string correlationId,
        [Description("Usefulness grade 1-5 (5=best).")]
        int grade,
        [Description("Optional note explaining the grade.")]
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnSetQueryResultGrade, cancellationToken);

        var recorded = await qualityService.RecordGradeAsync(canonical, correlationId, grade, note, cancellationToken);
        var envelope = await gate.WrapAsync(canonical, new GradeResult(recorded), cancellationToken);

        return envelope;
    }

    [McpServerTool(Name = TnGetQualityReport)]
    [McpToolScope(ToolScope.All)]
    [Description("Aggregated search-quality metrics: total searches, follow-through searches, graded searches, average grade, coverage rate, searches/day. Window defaults to last 7 days (absolute DateTimeOffset). Project id defaults to working-directory project. Aggregate — no per-row grades included.")]
    public async Task<ApiEnvelope<SearchQualityMetrics>> GetQualityReport(
        [Description("The project id.")][Optional][DefaultParameterValue("")] string projectId,
        [Description("Window start as absolute DateTimeOffset; defaults to 7 days before now.")]
        DateTimeOffset? from = null,
        CancellationToken ct = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnGetQualityReport, ct);
        var metrics = await qualityService.GetMetricsAsync(canonical, from ?? DateTimeOffset.UtcNow.AddDays(-7), ct);
        return await gate.WrapAsync(canonical, metrics, ct);
    }

    public sealed record FollowThroughResult(bool Recorded);

    public sealed record GradeResult(bool Recorded);
}
