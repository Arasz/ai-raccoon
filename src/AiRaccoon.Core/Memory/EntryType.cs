namespace AiRaccoon.Core.Memory;

/// <summary>
///     What kind of entry a row holds. The bank holds three kinds, and the type decides which
///     corpus a read reaches and what a row may carry: a TTL belongs to a memory, never to an
///     ingested document (a document is a mirror of a file on disk, re-derivable by re-ingesting)
///     and never to a code chunk (a re-derivable cache).
/// </summary>
[Flags]
public enum EntryType
{
    /// <summary>No type: the row has not been classified (a legacy row the migration has not reached).</summary>
    None = 0,

    /// <summary>An agent-written memory — a note, or a session transcript the agent wrote.</summary>
    Memory = 1,

    /// <summary>A code chunk: the code corpus, a separate table with its own index.</summary>
    Code = 2,

    /// <summary>An ingested document: a file's chunk, mirroring a file on disk.</summary>
    File = 4
}
