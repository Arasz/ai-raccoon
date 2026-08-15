using AiRaccoon.Core.Metrics;
using System.ComponentModel;
using AiRaccoon.Core.Access;
using AiRaccoon.Infrastructure.Metrics;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tool over MetricsReportService — no business logic here (ADR-0065, mcp-thin).</summary>
public sealed class PerformanceTools(IMetricsReportService reportService, IToolGate gate)
{
    private const string TnMemoryPerformance = "memory_performance";

    [McpServerTool(Name = TnMemoryPerformance)]
    [Description(
        "Reports this project's self-instrumentation: one series per tool on the server's tool inventory, each with count/p50/p95/p99/min/max plus a bucketed time series. Defaults to the last 3 hours in 1-minute buckets (180 points); a bucket wider than the window clamps to a single averaged point. A tool never called still appears, at count 0. A quiet window is an empty series, never an error.")]
    public async Task<ApiEnvelope<PerformanceReport>> Performance(
        [Description("The project id.")] string projectId,
        [Description("Window size in minutes; defaults to 180 (3 hours).")]
        int? windowMinutes = null,
        [Description("Bucket size in minutes; defaults to 1. Clamped to the window when wider.")]
        int? bucketMinutes = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Read, TnMemoryPerformance, cancellationToken);
        var report = await reportService.GetReportAsync(projectId, McpToolInventory.Names(),
            windowMinutes is { } w ? TimeSpan.FromMinutes(w) : null,
            bucketMinutes is { } b ? TimeSpan.FromMinutes(b) : null,
            cancellationToken);
        var envelope = await gate.WrapAsync(projectId, report, cancellationToken);
        return envelope;
    }
}
