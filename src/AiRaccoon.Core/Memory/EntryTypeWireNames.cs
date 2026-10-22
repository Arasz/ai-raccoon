namespace AiRaccoon.Core.Memory;

/// <summary>
///     Single source for the QUERY PARAMETER's vocabulary: which kinds a query asks for, and how
///     the caller spells them. The words it shares with the corpus vocabulary come from there, so a
///     rename moves both at once.
///     <para>
///         The parameter is a flag set: a caller can ask for Memory alone, Code alone, File alone,
///         or any sum. The set lives here and nowhere else — the stored kind is its own
///         single-valued type, and the two meet only where a kind is written to the column, which
///         takes one kind and refuses a set.
///     </para>
/// </summary>
public static class EntryTypeWireNames
{
    /// <summary>An agent-written memory — a note, or a session transcript the agent wrote.</summary>
    public const string Memory = SearchKindWireNames.Memory;

    /// <summary>The code corpus: a separate table with its own FTS and vector index.</summary>
    public const string Code = SearchKindWireNames.Code;

    /// <summary>An ingested document: a file's chunk, mirroring a file on disk.</summary>
    public const string File = "file";

    /// <summary>
    ///     The set a query asks for when the caller names none: every kind, so the default answers
    ///     exactly what today's default (<c>kind=both</c>) answers, and more.
    /// </summary>
    public const EntryType Default = EntryType.Memory | EntryType.Code | EntryType.File;

    /// <summary>Every bit a kind is made of, and nothing else.</summary>
    private const EntryType KnownBits = Default;

    /// <summary>
    ///     The set a wire value names: a comma-separated list of kind names, read the way the rest
    ///     of the surface reads them — case-insensitively, ignoring spacing.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     The value names a kind this build does not know, or asks for nothing at all. Both are
    ///     refused rather than guessed at: an unknown name is a typo, and the empty set is not a
    ///     question.
    /// </exception>
    public static EntryType Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The entry type is required: expected memory, code, or file.",
                nameof(value));
        }

        var set = EntryType.None;
        foreach (var name in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            set |= name.ToLowerInvariant() switch
            {
                Memory => EntryType.Memory,
                Code => EntryType.Code,
                File => EntryType.File,
                _ => throw new ArgumentException(
                    $"Invalid entry type '{name}': expected memory, code, or file.", nameof(value))
            };
        }

        if (set == EntryType.None)
        {
            throw new ArgumentException(
                "The entry type asks for no kind at all: expected memory, code, or file.", nameof(value));
        }

        return RefuseUnknownBits(set);
    }

    /// <summary>
    ///     The one kind a lookup names. A hash alone does not say which corpus holds it, so a
    ///     composite lookup is refused rather than resolved to an arbitrary corpus.
    /// </summary>
    /// <exception cref="ArgumentException">The set names more than one kind.</exception>
    public static EntryType Single(EntryType types)
    {
        if (types == EntryType.None || (types & (types - 1)) != EntryType.None)
        {
            throw new ArgumentException(
                $"A lookup names one entry type, not '{types}'.", nameof(types));
        }

        return types;
    }

    /// <summary>
    ///     A set carrying a bit this build does not know is refused. A name can only ever produce a
    ///     known bit, so this guards the other door: a set assembled elsewhere that names a kind the
    ///     server has no corpus for.
    /// </summary>
    /// <exception cref="ArgumentException">The set carries a bit outside the known kinds.</exception>
    public static EntryType RefuseUnknownBits(EntryType types)
    {
        if ((types & ~KnownBits) != EntryType.None)
        {
            throw new ArgumentException(
                $"Unknown entry type bits in '{types}'.", nameof(types));
        }

        return types;
    }
}
