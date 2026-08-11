namespace AiRaccoon.Core.Memory;

/// <summary>
///     Canonical source identity row (see docs/work/2026-08-11-memory-source-normalization-plan.md §2).
///     Equality is based on the natural key (SourceType, SourceLocator, Section), not the row Id.
/// </summary>
public sealed record MemorySource
{
    private static readonly HashSet<SourceType> ValidTypes =
        [SourceType.File, SourceType.Transcript, SourceType.Manual];

    private SourceType _sourceType;
    private string _sourceLocator = "";

    /// <summary>Database row id; not part of equality.</summary>
    public long Id { get; init; }

    public required SourceType SourceType
    {
        init
        {
            if (!ValidTypes.Contains(value))
            {
                throw new ArgumentException($"Invalid SourceType value: {value}.", nameof(SourceType));
            }

            _sourceType = value;
        }
    }

    public required string SourceLocator
    {
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(SourceLocator));
            _sourceLocator = value;
        }
    }

    public string? Section { get; init; }

    public string? HeadingPath { get; init; }

    // Equality: natural key only (SourceType, SourceLocator, Section).
    public bool Equals(MemorySource? other) =>
        other is not null
        && _sourceType == other._sourceType
        && string.Equals(_sourceLocator, other._sourceLocator, StringComparison.Ordinal)
        && string.Equals(Section, other.Section, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(_sourceType, _sourceLocator, Section);
}
