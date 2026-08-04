using System.Text.Json.Serialization;

namespace AiRaccoon.Core.Watch;

/// <summary>Live watch status states (D4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WatchState
{
    Scanning,
    Healthy,
    Retrying,
    Stopped
}

/// <summary>Live view of one registered watch: state, last error, last sync (runtime-only, not persisted).</summary>
public sealed record WatchStatus(
    string ProjectId,
    string Path,
    WatchState State,
    string? LastError = null,
    DateTimeOffset? LastSync = null);
