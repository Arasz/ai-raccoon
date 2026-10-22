namespace AiRaccoon.Core.Memory;

/// <summary>
///     The kinds a query asks for. A flag set, because a caller can ask for Memory alone, Code
///     alone, File alone, or any sum; it replaces the old <c>kind</c> parameter
///     (<c>memory|code|both</c>).
///     <para>
///         This is the QUERY PARAMETER's vocabulary. It is not what a row holds: a row holds
///         exactly one kind (<see cref="EntryKind" />), and the two meet only where a kind is
///         written to the column, which takes a single kind and refuses a set.
///     </para>
/// </summary>
[Flags]
public enum EntryType
{
    /// <summary>No kind at all — the empty set, not a row's kind.</summary>
    None = 0,

    /// <summary>An agent-written memory — a note, or a session transcript the agent wrote.</summary>
    Memory = 1,

    /// <summary>
    ///     The code corpus: a separate table with its own FTS and vector index. A code chunk is
    ///     not a row of <c>entries</c>, so this names a corpus the query reaches, never a kind a
    ///     row carries.
    /// </summary>
    Code = 2,

    /// <summary>An ingested document: a file's chunk, mirroring a file on disk.</summary>
    File = 4
}
