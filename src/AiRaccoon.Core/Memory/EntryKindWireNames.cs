namespace AiRaccoon.Core.Memory;

/// <summary>
///     Single source for the KIND vocabulary — the stored value of <c>entries.entry_type</c>. A
///     rename must update this class, not the literals that spell it.
///     <para>
///         The words are shared with the corpus vocabulary on purpose: a row of kind
///         <c>memory</c> lives in the corpus <c>memory</c>, so the two spellings are one word and
///         move together.
///     </para>
/// </summary>
public static class EntryKindWireNames
{
    /// <summary>The row carries no kind yet.</summary>
    public const string None = "none";

    /// <summary>An agent-written memory — a note, or a session transcript the agent wrote.</summary>
    public const string Memory = SearchKindWireNames.Memory;

    /// <summary>An ingested document: a file's chunk, mirroring a file on disk.</summary>
    public const string File = "file";
}
