namespace AiRaccoon.Core.Memory;

/// <summary>
///     Canonical source identity row (see docs/work/2026-08-11-memory-source-normalization-plan.md §2).
///     Equality is based on the natural key (SourceType, SourceLocator, Section), not the row Id.
/// </summary>
public sealed record MemorySource
{
    private static readonly HashSet<SourceType> ValidTypes =
        [SourceType.File, SourceType.Transcript, SourceType.Manual];

    /// <summary>Database row id; not part of equality.</summary>
    public long Id { get; init; }

    public required SourceType SourceType
    {
        get;
        init
        {
            if (!ValidTypes.Contains(value))
            {
                throw new ArgumentException($"Invalid SourceType value: {value}.", nameof(SourceType));
            }

            field = value;
        }
    }

    public required string SourceLocator
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    public string? Section { get; init; }

    public string? HeadingPath { get; init; }

    // Equality: natural key only (SourceType, SourceLocator, Section).
    public bool Equals(MemorySource? other) =>
        other is not null
        && SourceType == other.SourceType
        && string.Equals(SourceLocator, other.SourceLocator, StringComparison.Ordinal)
        && string.Equals(Section, other.Section, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(SourceType, SourceLocator, Section);
}
