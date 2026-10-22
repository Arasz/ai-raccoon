namespace AiRaccoon.Core.Memory;

/// <summary>
///     Resolves the kind a row holds: the kind its writer declared, or — for a row that carries
///     none — the kind the row's own shape implies.
///     <para>
///         All the inputs are nullable, and each for a measured reason.
///     </para>
///     <para>
///         <paramref name="storedKind" /> is null or <c>none</c> for a row that carries no kind
///         yet: the v20 backfill fills those from the row's shape, so a missing kind is the signal
///         to fall back, not an error.
///     </para>
///     <para>
///         <paramref name="path" /> reaches the resolver unfiltered, because the backfill's row
///         query does not exclude a null path.
///     </para>
///     <para>
///         <paramref name="sourceFile" /> is null for a row with no source file — 215 of the
///         25,340 rows on the live bank — which is the ordinary shape of an agent-written note.
///     </para>
/// </summary>
public interface IEntryKindClassifier
{
    /// <summary>
    ///     The kind a row holds. A declared kind is preserved; a row with no declared kind is
    ///     classified by its own shape.
    /// </summary>
    EntryKind Resolve(string? storedKind, string? path, string? sourceFile);

    /// <summary>
    ///     The kind a row holds, from the row's own shape — the fallback for a row that carries no
    ///     kind. A writer declares its row's kind; this is what a row without a declaration is
    ///     filed as.
    /// </summary>
    EntryKind Classify(string? path, string? sourceFile);

    /// <summary>
    ///     The stored form of a kind: the wire name, or <c>none</c> for the unclassified kind. A
    ///     set of kinds has no stored form and is refused.
    /// </summary>
    string ToStoredValue(EntryKind kind);

    /// <summary>The kind a stored value names; <see cref="EntryKind.None" /> for null or an unknown value.</summary>
    EntryKind FromStoredValue(string? value);

    /// <summary>
    ///     A stored kind, read as one of its THREE states: a kind the row declares, a row that
    ///     declares none, or a value this build does not know.
    /// </summary>
    StoredKind ReadStoredKind(string? value);

    /// <summary>
    ///     The stored value a rewritten row keeps: its declared kind, or the kind its shape gives
    ///     for an undeclared row. An unknown stored value is carried through untouched — it is a
    ///     defect the doctor counts, and only the undeclared state may be resolved from the shape.
    /// </summary>
    string ResolveStoredValue(string? storedKind, string? path, string? sourceFile);
}
