using System.Text.Json;
using AiRaccoon.Core.Projects;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Ingest scope allowlist keys: global + per-project, more specific wins; values are JSON arrays
///     of absolute, normalized paths. Binds every path-reading surface (ingest, watch).
/// </summary>
public static class IngestScopeKeys
{
    public const string ScopeGlobal = "ingest.scope.global";

    /// <summary>The pre-1.2 key prefix; banks are migrated on open, and nothing writes it.</summary>
    public const string LegacyScopePrefix = "watch.scope.";

    /// <summary>
    ///     The per-project scope key. The id segment folds at construction (air-merge P4, defense in
    ///     depth — the authoritative fold is the ToolGate choke): a loser id must never again be
    ///     embedded raw or the resolver would see two projects where the repair left one.
    /// </summary>
    public static string ScopeProject(string projectId) =>
        $"ingest.scope.{ProjectIdAliasMap.Default.Fold(projectId)}";

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
