namespace AiRaccoon.Observability;

/// <summary>
///     Single source of truth for meter/ActivitySource scope names and OTel instrument names
///     (docs/work/2026-08-09-otlp-fix-plan.md "Naming consolidation"). OtlpExport and
///     MonitoringCommandRenderer derive their meter/provider lists from this instead of repeating the
///     literals; the guard in OtlpNamesRegistryTests keeps it honest against what the DI container
///     actually creates.
/// </summary>
public static class OtlpNames
{
    public const string MemoryToolsScope = "AiRaccoon.MemoryTools";
    public const string PromotionQueueScope = "AiRaccoon.PromotionQueue";

    /// <summary>
    ///     Background passes (WP13). Separate from the tool scope so `dotnet-trace --providers
    ///     AiRaccoon.MemoryTools` keeps meaning "tool calls" and nothing else.
    /// </summary>
    public const string BackgroundScope = "AiRaccoon.Background";

    public const string RuntimeScope = "System.Runtime";

    /// <summary>
    ///     ASP.NET Core's own ActivitySource for the per-request hosting Activity (docs/adr/0021):
    ///     registered so the tool span's parent is recorded and exported instead of dangling. This is
    ///     "Microsoft.AspNetCore" — the *ActivitySource* the framework creates HttpRequestIn on
    ///     (dotnet/aspnetcore GenericWebHostBuilder.cs). "Microsoft.AspNetCore.Hosting" is a different
    ///     signal, the hosting *Meter* name (HostingMetrics.cs) — ADR-0021 names the wrong one.
    /// </summary>
    public const string AspNetCoreScope = "Microsoft.AspNetCore";

    /// <summary>
    ///     Self-instrumenting since .NET 9 (docs/work/2026-08-09-otlp-fix-plan.md WP12) — no
    ///     OpenTelemetry.Instrumentation.Http package. Covers embedding-endpoint and Azure Blob calls,
    ///     since Blob traffic rides HttpClient too.
    /// </summary>
    public const string HttpScope = "System.Net.Http";

    public const string ToolInvocations = "ai_raccoon.tool.invocations";

    /// <summary>The MCP semantic convention's own name for this measurement (owner ruling D2: adopt).</summary>
    public const string ToolDuration = "mcp.server.operation.duration";

    public const string QueueQueued = "ai_raccoon.queue.queued";
    public const string QueueEvictions = "ai_raccoon.queue.evictions";
    public const string QueueEvictedScore = "ai_raccoon.queue.eviction.score";
    public const string QueueWait = "ai_raccoon.queue.wait";
    public const string QueuePromoted = "ai_raccoon.queue.promoted";
    public const string QueueDiscarded = "ai_raccoon.queue.discarded";
    public const string QueuePruned = "ai_raccoon.queue.pruned";
    public const string QueuePromoteFailures = "ai_raccoon.queue.promote_failures";
    public const string QueueCapacityUtilization = "ai_raccoon.queue.capacity.utilization";

    public const string BackgroundPasses = "ai_raccoon.background.passes";
    public const string BackgroundPassDuration = "ai_raccoon.background.pass.duration";

    /// <summary>Every meter the process registers for OTLP export.</summary>
    public static readonly IReadOnlyList<string> Meters =
        [MemoryToolsScope, PromotionQueueScope, BackgroundScope, RuntimeScope, HttpScope];

    /// <summary>Every ActivitySource the process registers for OTLP export.</summary>
    public static readonly IReadOnlyList<string> Sources =
        [MemoryToolsScope, BackgroundScope, AspNetCoreScope, HttpScope];
}
