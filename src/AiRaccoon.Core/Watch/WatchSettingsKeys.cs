namespace AiRaccoon.Core.Watch;

/// <summary>
///     Settings-table keys for file-watcher configuration. EXACT CONTRACT for the
///     file-watcher feature: project-specific rows override the global row; enabled
///     defaults to "false", concurrency to 4; scope rows are JSON arrays of absolute
///     paths (Path.GetFullPath semantics).
/// </summary>
public static class WatchSettingsKeys
{
    public const string EnabledGlobal = "watch.enabled.global";

    public const string ScopeGlobal = "watch.scope.global";

    public const string ConcurrencyGlobal = "watch.concurrency.global";

    public static string Enabled(string projectId) => $"watch.enabled.{projectId}";

    public static string Scope(string projectId) => $"watch.scope.{projectId}";

    public static string Concurrency(string projectId) => $"watch.concurrency.{projectId}";
}
