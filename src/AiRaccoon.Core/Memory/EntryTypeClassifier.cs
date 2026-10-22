namespace AiRaccoon.Core.Memory;

/// <summary>
///     The one rule that says which entry type a row holds. Everything that classifies — the
///     write paths, the migration's backfill, the query path's filter — goes through here, so
///     there is one answer to "is this row a memory or a document" in the codebase.
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
    ///     The entry type a row holds, from the row's own shape.
    ///     <para>
    ///         A row that MIRRORS a file on disk has <c>path = source_file</c>: the file's path is
    ///         both where it came from and what it is. Every other row in the entries table is
    ///         agent-written: its path is a synthetic <c>&lt;sha256&gt;.md</c> and its source file is
    ///         a LOCATOR — a transcript it came from, or a file it cites.
    ///     </para>
    ///     <para>
    ///         The shape is the test, not the provenance channel: a note that cites
    ///         <c>notes/probe.md</c> has a source file and is still a memory — the agent wrote it,
    ///         and re-ingesting the file would not bring it back. The write path's own taxonomy
    ///         (<c>ClassifySource</c>) cannot tell the two apart: it calls anything with a
    ///         non-transcript source file a file, so 654 of 2,150 agent notes on a live bank would
    ///         have been filed as documents.
    ///     </para>
    /// </summary>
    public static EntryType Classify(string? path, string? sourceFile) =>
        !string.IsNullOrEmpty(sourceFile) && string.Equals(path, sourceFile, StringComparison.Ordinal)
            ? EntryType.File
            : EntryType.Memory;

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
