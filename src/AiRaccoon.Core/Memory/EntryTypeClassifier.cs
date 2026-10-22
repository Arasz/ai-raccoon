namespace AiRaccoon.Core.Memory;

/// <summary>Resolves a declared entry kind, falling back to a legacy row's path and source file.</summary>
public sealed class EntryTypeClassifier : IEntryTypeClassifier
{
    /// <summary>
    ///     The kind a row holds. A declared kind wins; a row with none is classified by its own
    ///     shape. The stored value is read through <see cref="FromStoredValue" />, so a row whose
    ///     stored value is not a kind this build knows falls back to the shape rather than
    ///     carrying a value nothing can read.
    /// </summary>
    public EntryType Resolve(string? storedType, string? path, string? sourceFile)
    {
        var declared = FromStoredValue(storedType);
        return declared != EntryType.None ? declared : Classify(path, sourceFile);
    }

    /// <summary>The wire name of a memory.</summary>
    public const string MemoryName = "memory";

    /// <summary>The wire name of a code chunk.</summary>
    public const string CodeName = "code";

    /// <summary>The wire name of an ingested document.</summary>
    public const string FileName = "file";

    /// <summary>
    ///     The entry type a row holds, from the row's own shape — the FALLBACK for a row that
    ///     predates the column (the migration's rule), not what a writer uses. A writer DECLARES
    ///     its row's kind: the write path knows it is writing an agent's memory and the ingest
    ///     path knows it is mirroring a file. Deriving the kind from the shape instead would let a
    ///     caller name a note's own synthetic path (sha256 of the content) and have the note filed
    ///     as a document — the rule would then read a caller-supplied string.
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
    public EntryType Classify(string? path, string? sourceFile) =>
        !string.IsNullOrEmpty(sourceFile) && string.Equals(path, sourceFile, StringComparison.Ordinal)
            ? EntryType.File
            : EntryType.Memory;

    /// <summary>The stored form of a type: the wire name, or null for <see cref="EntryType.None" />.</summary>
    public string? ToStoredValue(EntryType type) => type switch
    {
        EntryType.Memory => MemoryName,
        EntryType.Code => CodeName,
        EntryType.File => FileName,
        _ => null
    };

    /// <summary>The type a stored value names; <see cref="EntryType.None" /> for null or an unknown value.</summary>
    public EntryType FromStoredValue(string? value) => value switch
    {
        MemoryName => EntryType.Memory,
        CodeName => EntryType.Code,
        FileName => EntryType.File,
        _ => EntryType.None
    };
}
