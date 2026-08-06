using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Watch integration tests drive a real FileSystemWatcher-backed pipeline against real
///     SQLite with wall-clock poll deadlines (StepUntilAsync). Under full-suite parallel load
///     the watcher + ingest + search can exceed the poll bound and fail spuriously, so the
///     class runs serially against all other tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WatchIntegrationCollection
{
    public const string Name = "watch-integration";
}
