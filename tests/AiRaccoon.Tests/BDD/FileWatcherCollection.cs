using Xunit;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     The file-watcher BDD feature drives a real FileSystemWatcher-backed pipeline against
///     real SQLite with a fake-time step budget (StepUntilAsync; no wall-clock verdict, PR #464). Under
///     full-suite parallel load the watcher + ingest + search can exceed the poll bound and
///     fail spuriously (Class B flake), so the feature runs serially against all other tests —
///     same treatment as WatchIntegrationCollection. Serialising this suite does not bound what
///     else runs on the machine, which is why the hang-stop is 30s rather than 5s.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileWatcherCollection
{
    public const string Name = "file-watcher-bdd";
}
