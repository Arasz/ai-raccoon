using System.ComponentModel;
using System.Runtime.InteropServices;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Watch;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IWatchService — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class WatchTools(
    IWatchService watch,
    IToolGate gate)
{
    internal const string TnAddWatch = "add_watch";
    internal const string TnGetWatchStatus = "get_watch_status";
    internal const string TnRemoveWatch = "remove_watch";

    [McpServerTool(Name = TnAddWatch)]
    [McpToolScope(ToolScope.All)]
    [Description(
        "Registers a file or directory to be mirrored into the project's memory. Pass projectId=shared explicitly for documents available to all projects in this bank; no ID minting or registration needed. Watching must be enabled and the path inside the scope allowlist — both configured via the CLI ('ai-raccoon settings watch enable' / 'settings ingest scope add'). No overlapping watches: a path already covered by an existing watch is refused (watch-overlap, naming the covering watch); registering a broader watch prunes every watch it contains (reported in `pruned`; already-ingested entries are kept and the broader watch re-scans them). An exact re-add of an already-watched path is a no-op (`absorbedBy` reports it). Returns immediately — the initial scan runs in the background (status reports scanning).")]
    public async Task<ApiEnvelope<WatchAddResult>> Add(
        [Description("The project id; use shared explicitly for documents shared across projects.")]
        [Optional][DefaultParameterValue("")] string projectId,
        [Description("Absolute path of the file or directory to watch.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnAddWatch, cancellationToken);

        var outcome = await watch.AddAsync(canonical, path, cancellationToken);
        var envelope = await gate.WrapAsync(canonical,
            new WatchAddResult(canonical, path, outcome.Pruned, outcome.AbsorbedBy), cancellationToken);

        return envelope;
    }

    [McpServerTool(Name = TnGetWatchStatus)]
    [McpToolScope(ToolScope.All)]
    [Description(
        "Lists the project's registered watches with their live state (scanning/healthy/retrying/stopped), last error, last sync, and the files the watch could not index: skippedCount plus up to 20 skipped entries (path, reason unsupported-type/binary/unparseable). Empty list when none. Available in every access tier.")]
    public async Task<ApiEnvelope<WatchStatusResult>> Status(
        [Description("The project id.")] string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnGetWatchStatus, cancellationToken);

        var states = await watch.StatusAsync(canonical, cancellationToken);
        var envelope = await gate.WrapAsync(canonical, new WatchStatusResult(states), cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnRemoveWatch)]
    [McpToolScope(ToolScope.All)]
    [Description("Stops watching a path for the project and removes its registration; a non-existent watch is a no-op, reported as removed=false.")]
    public async Task<ApiEnvelope<WatchRemoveResult>> Remove(
        [Description("The project id.")][Optional][DefaultParameterValue("")] string projectId,
        [Description("Absolute path of the watched file or directory.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnRemoveWatch, cancellationToken);

        var removed = await watch.RemoveAsync(canonical, path, cancellationToken);
        var envelope = await gate.WrapAsync(canonical, new WatchRemoveResult(canonical, path, removed), cancellationToken);
        return envelope;
    }

    /// <summary>Pruned/AbsorbedBy are additive (docs/work/2026-08-21-code-search-implementation-plan.md
    /// §5 WP4): Pruned lists watches this add contained (empty when none); AbsorbedBy is set only
    /// for an exact-literal-path re-add (never together with a non-empty Pruned).</summary>
    public sealed record WatchAddResult(string ProjectId, string Path, IReadOnlyList<string> Pruned, string? AbsorbedBy);

    public sealed record WatchStatusResult(IReadOnlyList<WatchStatus> Watches);

    public sealed record WatchRemoveResult(string ProjectId, string Path, bool Removed);
}
