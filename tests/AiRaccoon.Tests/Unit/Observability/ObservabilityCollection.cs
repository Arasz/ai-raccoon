using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     Tests that attach ActivityListeners to the global "AiRaccoon.MemoryTools"
///     ActivitySource must not run in parallel: a listener's ActivityStopped
///     callback observes every activity stopped on that source, so a concurrent
///     test's activity can clobber the captured reference (observed flake:
///     ErrorRecord_SetsErrorStatusOnActivity intermittently saw the sibling
///     class's memory_sync activity). Serializing the classes keeps each
///     listener's observations scoped to its own test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ObservabilityCollection
{
    public const string Name = "Observability";
}
