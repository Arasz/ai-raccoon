using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>Watch identity: ordinal project id, host-OS path case via <see cref="IngestPath.PathComparer" />.</summary>
internal readonly record struct WatchKey(string ProjectId, string Path)
{
    public bool Equals(WatchKey other) =>
        StringComparer.Ordinal.Equals(ProjectId, other.ProjectId) && IngestPath.PathComparer.Equals(Path, other.Path);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.Ordinal.GetHashCode(ProjectId), IngestPath.PathComparer.GetHashCode(Path));
}
