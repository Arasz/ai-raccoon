namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     Constants mirroring the code corpus's fixed vec_code shape (MemorySchema.cs:
///     <c>vec0(ctx TEXT, embedding float[768] ...)</c>). Unlike the memory bank, code has no
///     dimension-reconcile phase (§3.3 D-E9) — this is the only gate protecting vec_code, so a
///     manifest declaring a different dimension is refused at configure time instead.
/// </summary>
public static class CodeCorpusSchema
{
    public const int EmbeddingDimensions = 768;
}
