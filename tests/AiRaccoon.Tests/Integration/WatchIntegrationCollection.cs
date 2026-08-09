using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Watch integration tests drive a real FileSystemWatcher-backed pipeline against real SQLite
///     with wall-clock poll deadlines, which can fail spuriously under full-suite parallel load;
///     this class runs serially against all other tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WatchIntegrationCollection
{
    public const string Name = "watch-integration";
}
