using System.Text.Json;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Settings keys for the ingest scope allowlist — the paths a project may read from disk.
///     Global + per-project, more specific wins. Values are JSON arrays of absolute, normalized
///     paths. Every path-reading surface is bound by it: memory_ingest_file,
///     memory_ingest_directory, memory_watch_add and the file watcher.
/// </summary>
public static class IngestScopeKeys
{
    public const string ScopeGlobal = "ingest.scope.global";

    /// <summary>The pre-1.2 key prefix; banks are migrated on open, and nothing writes it.</summary>
    public const string LegacyScopePrefix = "watch.scope.";

    public static string ScopeProject(string projectId) => $"ingest.scope.{projectId}";

    public static string Serialize(IEnumerable<string> paths)
    {
        Guard.IsNotNull(paths);
        return JsonSerializer.Serialize(paths.Select(IngestPath.Normalize).ToArray());
    }

    /// <summary>Parses a stored scope value; null when absent or malformed (caller falls back to the global entry).</summary>
    public static IReadOnlyList<string>? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value)?.Select(IngestPath.Normalize).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
