using System.Text.Json.Serialization;
using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Core.Watch;

/// <summary>Live watch status states (docs/plans/file-watcher-implementation.md D4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WatchState
{
    Scanning,
    Healthy,
    Retrying,
    Stopped
}

/// <summary>
///     Live view of one registered watch: state, last error, last sync, and the files its digests
///     skipped — <see cref="SkippedCount" /> is exact, <see cref="Skipped" /> the first
///     <see cref="MaxListedSkips" /> by path (runtime-only, not persisted).
/// </summary>
public sealed record WatchStatus(
    string ProjectId,
    string Path,
    WatchState State,
    string? LastError = null,
    DateTimeOffset? LastSync = null,
    int SkippedCount = 0,
    IReadOnlyList<SkippedFile>? Skipped = null)
{
    /// <summary>How many skipped files a status lists.</summary>
    public const int MaxListedSkips = 20;

    public IReadOnlyList<SkippedFile> Skipped { get; init; } = Skipped ?? [];

    /// <summary>Value equality, comparing <see cref="Skipped" /> element by element.</summary>
    public bool Equals(WatchStatus? other) =>
        other is not null
        && ProjectId == other.ProjectId
        && Path == other.Path
        && State == other.State
        && LastError == other.LastError
        && LastSync == other.LastSync
        && SkippedCount == other.SkippedCount
        && Skipped.SequenceEqual(other.Skipped);

    public override int GetHashCode() => HashCode.Combine(ProjectId, Path, State, LastError, LastSync, SkippedCount);
}

/// <summary>A file a watch digest indexed nothing for, and why.</summary>
public sealed record SkippedFile(string Path, SkipReason Reason);
