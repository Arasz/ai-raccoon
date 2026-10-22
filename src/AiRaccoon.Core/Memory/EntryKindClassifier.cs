namespace AiRaccoon.Core.Memory;

/// <summary>
///     Resolves the kind a row holds: the kind its writer declared, or — for a row that carries
///     none — the kind the row's own shape implies.
/// </summary>
public sealed class EntryKindClassifier : IEntryKindClassifier
{
    /// <summary>
    ///     The kind a row holds. A declared kind wins; a row with none is classified by its own
    ///     shape. The stored value is read through <see cref="FromStoredValue" />, so a row whose
    ///     stored value is not a kind this build knows falls back to the shape rather than
    ///     carrying a value nothing can read.
    /// </summary>
    public EntryKind Resolve(string? storedKind, string? path, string? sourceFile)
    {
        var declared = FromStoredValue(storedKind);
        return declared != EntryKind.None ? declared : Classify(path, sourceFile);
    }

    /// <summary>
    ///     The kind a row holds, from the row's own shape — the FALLBACK for a row that carries no
    ///     kind, not what a writer uses. A writer DECLARES its row's kind: the write path knows it
    ///     is writing an agent's memory and the ingest path knows it is mirroring a file. Deriving
    ///     the kind from the shape instead would let a caller name a note's own synthetic path
    ///     (sha256 of the content) and have the note filed as a document — the rule would then read
    ///     a caller-supplied string.
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
    public EntryKind Classify(string? path, string? sourceFile) =>
        !string.IsNullOrEmpty(sourceFile) && string.Equals(path, sourceFile, StringComparison.Ordinal)
            ? EntryKind.File
            : EntryKind.Memory;

    /// <summary>
    ///     The stored form of a kind: the wire name, or <c>none</c> for the unclassified kind. A
    ///     SET has no stored form — a row holds one kind — so a set is refused loudly rather than
    ///     written as a value nothing can read.
    /// </summary>
    public string ToStoredValue(EntryKind kind) => kind switch
    {
        EntryKind.None => EntryKindWireNames.None,
        EntryKind.Memory => EntryKindWireNames.Memory,
        EntryKind.File => EntryKindWireNames.File,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "A row holds one kind; a set of kinds is the query parameter's vocabulary, not a row's")
    };

    /// <summary>The kind a stored value names; <see cref="EntryKind.None" /> for null or an unknown value.</summary>
    public EntryKind FromStoredValue(string? value) => value switch
    {
        EntryKindWireNames.Memory => EntryKind.Memory,
        EntryKindWireNames.File => EntryKind.File,
        _ => EntryKind.None
    };

    /// <summary>
    ///     A stored kind, read as one of its THREE states: a kind the row declares, a row that
    ///     declares none, or a value this build does not know.
    ///     <para>
    ///         The third state is the one that used to be lost: <see cref="FromStoredValue" /> maps
    ///         every value it does not know to the undeclared kind, so an unknown value and an
    ///         undeclared row read alike — and a rewrite then resolves the unknown one from the
    ///         shape, which erases the evidence. Reading the three apart is what lets the repair
    ///         path leave an unknown value alone and the doctor count it.
    ///     </para>
    /// </summary>
    public StoredKind ReadStoredKind(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == EntryKindWireNames.None)
        {
            return StoredKind.Undeclared;
        }

        return FromStoredValue(value) switch
        {
            EntryKind.Memory => StoredKind.Of(EntryKind.Memory),
            EntryKind.File => StoredKind.Of(EntryKind.File),
            _ => StoredKind.OfUnknown(value)
        };
    }

    /// <summary>
    ///     The stored value a rewritten row keeps. Three states, and they are not the same:
    ///     <list type="bullet">
    ///         <item>
    ///             a DECLARED kind — the row named one, and it is kept;
    ///         </item>
    ///         <item>
    ///             an UNDECLARED row (no kind, or the <c>none</c> marker) — its kind comes from its
    ///             shape;
    ///         </item>
    ///         <item>
    ///             an UNKNOWN value — a kind this build does not know, which is a defect the
    ///             doctor counts. It is carried through untouched: only the undeclared state may be
    ///             resolved from the shape, and normalising an unknown value away would leave the
    ///             doctor with nothing to report.
    ///         </item>
    ///     </list>
    /// </summary>
    public string ResolveStoredValue(string? storedKind, string? path, string? sourceFile)
    {
        var read = ReadStoredKind(storedKind);
        return read.State switch
        {
            // An unknown value is a defect: it is carried through untouched, because normalising it
            // away would leave the doctor with nothing to report.
            StoredKindState.Unknown => read.Raw!,
            _ => ToStoredValue(Resolve(storedKind, path, sourceFile))
        };
    }
}
