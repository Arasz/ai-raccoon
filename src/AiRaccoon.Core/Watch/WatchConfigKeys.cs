namespace AiRaccoon.Core.Watch;

/// <summary>
///     Settings keys for watch config. The exact strings are a contract with the CLI task's
///     `watch` commands (enable/concurrency, global + per-project; more specific wins).
///     The scope allowlist is not here: it bounds every disk-reading surface, not just
///     watching — see <see cref="AiRaccoon.Core.Ingestion.IngestScopeKeys" />.
/// </summary>
public static class WatchConfigKeys
{
    public const string EnabledGlobal = "watch.enabled.global";
    public const string ConcurrencyGlobal = "watch.concurrency.global";

    public static string EnabledProject(string projectId) => $"watch.enabled.{projectId}";

    public static string ConcurrencyProject(string projectId) => $"watch.concurrency.{projectId}";
}
