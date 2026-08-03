namespace AiRaccoon.Tests.TestSupport;

/// <summary>TimeProvider pinned to a fixed instant, for deterministic time-dependent tests.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
