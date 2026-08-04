using AiRaccoon.Core.Watch;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>Filesystem change kinds entering the pipeline (S5's event source produces these).</summary>
public enum WatchEventKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

/// <summary>One filesystem change entering the pipeline; paths are normalized per D3 before use.</summary>
public sealed record WatchEvent(string ProjectId, string Path, WatchEventKind Kind, string? OldPath = null);

/// <summary>A pending digest job: the event plus the registered watch it belongs to.</summary>
public sealed record WatchJob(WatchEvent Event, string WatchPath);

/// <summary>(projectId, path) identity comparer: host-OS path case, ordinal project id.</summary>
internal sealed class WatchKeyComparer : IEqualityComparer<(string ProjectId, string Path)>
{
    public static WatchKeyComparer Instance { get; } = new();

    public bool Equals((string ProjectId, string Path) x, (string ProjectId, string Path) y) =>
        StringComparer.Ordinal.Equals(x.ProjectId, y.ProjectId) && WatchPath.PathComparer.Equals(x.Path, y.Path);

    public int GetHashCode((string ProjectId, string Path) obj) =>
        HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.ProjectId), WatchPath.PathComparer.GetHashCode(obj.Path));
}
