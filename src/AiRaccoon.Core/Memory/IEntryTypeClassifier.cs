namespace AiRaccoon.Core.Memory;

/// <summary>
///     Resolves the kind a row holds: the kind its writer declared, or — for a row that predates
///     the column — the kind the row's own shape implies.
///     <para>
///         All three inputs are nullable, and each for a measured reason.
///     </para>
///     <para>
///         <paramref name="storedType" /> is null for a row written before the column existed:
///         the v20 migration backfills those from the row's shape. A null there is the signal to
///         fall back, not an error.
///     </para>
///     <para>
///         <paramref name="path" /> is null for a row with no path. The backfill's row query does
///         not filter on <c>path IS NOT NULL</c>, so a null path reaches the resolver.
///     </para>
///     <para>
///         <paramref name="sourceFile" /> is null for a row with no source file — 215 of the
///         25,340 rows on the live bank — which is the ordinary shape of an agent-written note.
///     </para>
/// </summary>
public interface IEntryTypeClassifier
{
    /// <summary>
    ///     The kind a row holds. A declared kind is preserved; a row with no declared kind is
    ///     classified by its own shape.
    /// </summary>
    EntryType Resolve(string? storedType, string? path, string? sourceFile);

    /// <summary>
    ///     The kind a row holds, from the row's own shape — the fallback for a row that predates
    ///     the column. A writer declares its row's kind; this is what a row without a declaration
    ///     is filed as.
    /// </summary>
    EntryType Classify(string? path, string? sourceFile);

    /// <summary>The stored form of a type: the wire name, or null for <see cref="EntryType.None" />.</summary>
    string? ToStoredValue(EntryType type);

    /// <summary>The type a stored value names; <see cref="EntryType.None" /> for null or an unknown value.</summary>
    EntryType FromStoredValue(string? value);
}
