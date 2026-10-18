using AiRaccoon.Core.Watch;

namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane watch resource (ADR-0075 amendment): `watch add`,
///     `watch remove` and `watch status`, shared by the endpoint and the CLI's client so the halves cannot drift.
/// </summary>
internal static class WatchProtocol
{
    public const string Path = "/watch";

    public static string ForStatus(string? projectId) =>
        projectId is null ? Path : $"{Path}?projectId={Uri.EscapeDataString(projectId)}";

    public static string ForRemove(string projectId, string path) =>
        $"{Path}?projectId={Uri.EscapeDataString(projectId)}&path={Uri.EscapeDataString(path)}";
}

internal sealed record WatchAddRequest(string ProjectId, string Path);

/// <summary>The memory_watch_add result fields: canonical project, requested path, pruned watches, covering watch of an exact re-add.</summary>
internal sealed record WatchAddResponse(string ProjectId, string Path, IReadOnlyList<string> Pruned, string? AbsorbedBy);

internal sealed record WatchRemoveResponse(string ProjectId, string Path);

internal sealed record WatchStatusResponse(IReadOnlyList<WatchStatus> Watches);

/// <summary>A refused watch call (HTTP 422): the MCP refusal prefix, the reason, and the covering watch of a watch-overlap.</summary>
internal sealed record WatchRefusal(string Kind, string Message, string? CoveringPath);
