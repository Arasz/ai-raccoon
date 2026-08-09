using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     Serializes tests using the global "AiRaccoon.MemoryTools" ActivitySource: a listener's
///     ActivityStopped callback fires for every activity on the source, so parallel tests can
///     clobber each other's captured activity.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ObservabilityCollection
{
    public const string Name = "Observability";
}
