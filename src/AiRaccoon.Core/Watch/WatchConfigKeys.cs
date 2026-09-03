namespace AiRaccoon.Core.Watch;

/// <summary>
///     Settings keys for watch config; the exact strings are a contract with the CLI task's
///     `watch` commands (enable/concurrency, global + per-project; more specific wins). The scope
///     allowlist is not here — see <see cref="AiRaccoon.Core.Ingestion.IngestScopeKeys" />.
/// </summary>
public static class WatchConfigKeys
{
    public const string EnabledGlobal = "watch.enabled.global";
    public const string ConcurrencyGlobal = "watch.concurrency.global";

    /// <summary>The per-project key; the id segment folds at construction (air-merge P4 — see <see cref="AiRaccoon.Core.Ingestion.IngestScopeKeys" />).</summary>
    public static string EnabledProject(string projectId) =>
        $"watch.enabled.{Projects.ProjectIdAliasMap.Default.Fold(projectId)}";

    /// <summary>The per-project key; the id segment folds at construction (air-merge P4 — see <see cref="AiRaccoon.Core.Ingestion.IngestScopeKeys" />).</summary>
    public static string ConcurrencyProject(string projectId) =>
        $"watch.concurrency.{Projects.ProjectIdAliasMap.Default.Fold(projectId)}";
}
