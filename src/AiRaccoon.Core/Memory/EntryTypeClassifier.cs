namespace AiRaccoon.Core.Memory;

/// <summary>
///     The one rule that says which entry type a row holds. Everything that classifies — the
///     write paths, the migration's backfill, the query path's filter — goes through here, so
///     there is one answer to "is this row a memory or a file" in the codebase.
/// </summary>
public static class EntryTypeClassifier
{
    /// <summary>The wire name of a memory.</summary>
    public const string MemoryName = "memory";

    /// <summary>The wire name of a code chunk.</summary>
    public const string CodeName = "code";

    /// <summary>The wire name of an ingested document.</summary>
    public const string FileName = "file";

    /// <summary>
    ///     The entry type a source produces. A transcript is a MEMORY: the agent wrote it, and the
    ///     session it came from is only the locator. The cheap signature (no source file, no
    ///     chunks) cannot say this — a transcript carries a source file and is chunked — which is
    ///     why the classification comes from the source's type and not from the row's shape.
    /// </summary>
    public static EntryType FromSource(SourceType sourceType) => FromSourceName(sourceType switch
    {
        SourceType.Manual => ManualName,
        SourceType.Transcript => TranscriptName,
        SourceType.File => FileSourceName,
        _ => null
    });

    /// <summary>
    ///     The same rule, from the source type's stored form. The stored form is what a repair
    ///     path reads (the row's source comes back as text), so the string is the table the rule
    ///     lives in and the enum form delegates to it.
    /// </summary>
    public static EntryType FromSourceName(string? sourceType) => sourceType switch
    {
        ManualName => EntryType.Memory,
        TranscriptName => EntryType.Memory,
        FileSourceName => EntryType.File,
        _ => EntryType.None
    };

    /// <summary>The stored name of a manual source.</summary>
    public const string ManualName = "manual";

    /// <summary>The stored name of a transcript source.</summary>
    public const string TranscriptName = "transcript";

    /// <summary>The stored name of a file source.</summary>
    public const string FileSourceName = "file";

    /// <summary>The stored form of a type: the wire name, or null for <see cref="EntryType.None" />.</summary>
    public static string? ToStoredValue(EntryType type) => type switch
    {
        EntryType.Memory => MemoryName,
        EntryType.Code => CodeName,
        EntryType.File => FileName,
        _ => null
    };

    /// <summary>The type a stored value names; <see cref="EntryType.None" /> for null or an unknown value.</summary>
    public static EntryType FromStoredValue(string? value) => value switch
    {
        MemoryName => EntryType.Memory,
        CodeName => EntryType.Code,
        FileName => EntryType.File,
        _ => EntryType.None
    };
}
