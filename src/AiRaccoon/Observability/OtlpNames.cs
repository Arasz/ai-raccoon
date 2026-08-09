namespace AiRaccoon.Observability;

/// <summary>Single source of truth for meter/ActivitySource scope names and OTel instrument names
/// (docs/work/2026-08-09-otlp-fix-plan.md "Naming consolidation"). OtlpExport and
/// MonitoringCommandRenderer derive their meter/provider lists from this instead of repeating the
/// literals; the guard in OtlpNamesRegistryTests keeps it honest against what the DI container
/// actually creates.</summary>
public static class OtlpNames
{
    public const string MemoryToolsScope = "AiRaccoon.MemoryTools";
    public const string PromotionQueueScope = "AiRaccoon.PromotionQueue";
    public const string RuntimeScope = "System.Runtime";

    /// <summary>ASP.NET Core's own ActivitySource for the per-request hosting Activity (docs/adr/0021):
    /// registered so the tool span's parent is recorded and exported instead of dangling. This is
    /// "Microsoft.AspNetCore" — the *ActivitySource* the framework creates HttpRequestIn on
    /// (dotnet/aspnetcore GenericWebHostBuilder.cs). "Microsoft.AspNetCore.Hosting" is a different
    /// signal, the hosting *Meter* name (HostingMetrics.cs) — ADR-0021 names the wrong one.</summary>
    public const string AspNetCoreScope = "Microsoft.AspNetCore";

    /// <summary>Every meter the process registers for OTLP export.</summary>
    public static readonly IReadOnlyList<string> Meters = [MemoryToolsScope, PromotionQueueScope, RuntimeScope];

    /// <summary>Every ActivitySource the process registers for OTLP export.</summary>
    public static readonly IReadOnlyList<string> Sources = [MemoryToolsScope, AspNetCoreScope];

    public const string ToolInvocations = "ai_raccoon.tool.invocations";

    /// <summary>The MCP semantic convention's own name for this measurement (owner ruling D2: adopt).</summary>
    public const string ToolDuration = "mcp.server.operation.duration";

    public const string QueueQueued = "ai_raccoon.queue.queued";
    public const string QueueEvictions = "ai_raccoon.queue.evictions";
    public const string QueueEvictedScore = "ai_raccoon.queue.eviction.score";
    public const string QueueWait = "ai_raccoon.queue.wait";
    public const string QueuePromoted = "ai_raccoon.queue.promoted";
    public const string QueueDiscarded = "ai_raccoon.queue.discarded";
    public const string QueueCapacityUtilization = "ai_raccoon.queue.capacity.utilization";
}
