using System.Text.Json.Serialization;

namespace AiRaccoon.Core.SearchQuality;

/// <summary>
///     One follow-through row: the file the agent read, plus the 1-based rank it was served at
///     when known (null when the caller never saw a rank). Stored as uniform object rows
///     (<c>{"path":"…","rank":3|null}</c>) in <c>search_quality.follow_through_files</c> — no DDL.
/// </summary>
public sealed record FollowThroughEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("rank")] int? Rank);
