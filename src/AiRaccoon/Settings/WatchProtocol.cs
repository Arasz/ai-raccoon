using System.Text.Json;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane watch resource (ADR-0075 amendment): `watch add`,
///     `watch remove` and `watch status`, shared by the endpoint and the CLI's client so the halves cannot drift.
/// </summary>
internal static class WatchProtocol
{
    public const string Path = "/watch";

    /// <summary>Problem-details extension members a 422 refusal carries.</summary>
    public const string KindExtension = "kind";

    public const string CoveringPathExtension = "coveringPath";

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

/// <summary>A refused watch call, read from its 422 problem details: the MCP refusal prefix, the reason, and the covering watch of a watch-overlap.</summary>
internal sealed record WatchRefusal(string Kind, string Message, string? CoveringPath)
{
    /// <summary>Reads a refusal from problem-details JSON; null when the body carries no <c>kind</c>.</summary>
    public static WatchRefusal? FromProblem(JsonElement problem) =>
        problem.ValueKind == JsonValueKind.Object
        && problem.TryGetProperty(WatchProtocol.KindExtension, out var kind) && kind.ValueKind == JsonValueKind.String
            ? new WatchRefusal(kind.GetString()!, Text(problem, "detail") ?? string.Empty, Text(problem, WatchProtocol.CoveringPathExtension))
            : null;

    private static string? Text(JsonElement problem, string name) =>
        problem.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
