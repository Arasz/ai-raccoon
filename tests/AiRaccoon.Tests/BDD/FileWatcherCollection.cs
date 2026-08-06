using Xunit;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     The file-watcher BDD feature drives a real FileSystemWatcher-backed pipeline against
///     real SQLite with wall-clock poll deadlines (StepUntilAsync, maxRealSeconds=5). Under
///     full-suite parallel load the watcher + ingest + search can exceed the poll bound and
///     fail spuriously (Class B flake), so the feature runs serially against all other tests —
///     same treatment as WatchIntegrationCollection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileWatcherCollection
{
    public const string Name = "file-watcher-bdd";
}
