namespace AiRaccoon.Core.Memory;

/// <summary>
///     The kind ONE row holds. An entry has exactly one type (the owner's ruling of 2026-10-22),
///     so the stored column holds a single kind rather than a set.
///     <para>
///         The flag set that lets a caller ask for several kinds at once belongs to the query
///         parameter alone (<see cref="EntryType" />); a row is never a sum. That is why this type
///         carries no flag attribute and no <c>Code</c> member: a code chunk is not a row of
///         <c>entries</c> — it lives in the separate <c>code_entries</c> corpus, which the query
///         parameter reaches as a corpus, not as a row kind.
///     </para>
/// </summary>
public enum EntryKind
{
    /// <summary>
    ///     No kind yet: the row has not been classified. It is the column's default, so a row the
    ///     backfill has not reached says so in the column rather than by a null.
    /// </summary>
    None = 0,

    /// <summary>An agent-written memory — a note, or a session transcript the agent wrote.</summary>
    Memory = 1,

    /// <summary>An ingested document: a file's chunk, mirroring a file on disk.</summary>
    File = 2
}
