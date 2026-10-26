namespace AiRaccoon.Core.Decisions;

/// <summary>A write expected a project revision that is no longer current.</summary>
public sealed class DecisionRevisionConflictException(long expectedRevision, long currentRevision)
    : Exception($"Decision revision conflict: expected {expectedRevision}, current {currentRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long CurrentRevision { get; } = currentRevision;
}
