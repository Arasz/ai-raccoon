namespace AiRaccoon.Core.Observability;

/// <summary>
///     Build-time identity of the running binary: version, commit sha and commit timestamp
///     stamped in by GitStamp.targets (docs/plans/2026-08-15-performance-metrics-implementation.md
///     section 7 R2). Commit degrades to "unknown" and <see cref="CommitTimestamp" /> to null —
///     never an empty string — when no git was available at build time.
/// </summary>
public interface IBuildStamp
{
    string Version { get; }

    string Commit { get; }

    DateTimeOffset? CommitTimestamp { get; }
}
