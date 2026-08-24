namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     Constants mirroring the code corpus's vec_code shape (MemorySchema.cs:
///     <c>vec0(ctx TEXT, embedding float[768] ...)</c>). 768 is the dimension a FRESH code corpus
///     is created at and legacy banks default to (vec-code-unfix-dim) — not a configure-time gate:
///     activation accepts any manifest dimension and reconciles vec_code to it.
/// </summary>
public static class CodeCorpusSchema
{
    public const int EmbeddingDimensions = 768;

    /// <summary>S2: a row that fails embedding this many times in a row is excluded from future
    /// drain selection (SelectAllPendingCodeForEmbed/HasPendingCodeEmbed) — a poison row must not
    /// starve its batch or retry every 15s maintenance poll forever.</summary>
    public const int MaxEmbedAttempts = 3;
}
