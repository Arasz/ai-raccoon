using System.Text.Json;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Watch;

/// <summary>
///     Settings keys for watch config. The exact strings are a contract with the CLI task's
///     `watch` commands (enable/scope/concurrency, global + per-project; more specific wins).
///     Scope values are JSON arrays of absolute, normalized paths.
/// </summary>
public static class WatchConfigKeys
{
    public const string EnabledGlobal = "watch.enabled.global";
    public const string ScopeGlobal = "watch.scope.global";
    public const string ConcurrencyGlobal = "watch.concurrency.global";

    public static string EnabledProject(string projectId) => $"watch.enabled.project:{projectId}";

    public static string ScopeProject(string projectId) => $"watch.scope.project:{projectId}";

    public static string ConcurrencyProject(string projectId) => $"watch.concurrency.project:{projectId}";

    public static string SerializeScope(IEnumerable<string> paths)
    {
        Guard.IsNotNull(paths);
        return JsonSerializer.Serialize(paths.Select(WatchPath.Normalize).ToArray());
    }

    /// <summary>Parses a stored scope value; null when absent or malformed (caller falls back to the global entry).</summary>
    public static IReadOnlyList<string>? ParseScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value)?.Select(WatchPath.Normalize).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
