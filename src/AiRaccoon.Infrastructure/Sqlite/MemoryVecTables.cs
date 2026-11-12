namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     One memory vec0 table: the entries column it embeds, the predicate selecting the rows that
///     carry a vector, and the <c>AFTER UPDATE</c> trigger that keeps it in step.
/// </summary>
public sealed record MemoryVecTable(
    string Table,
    string EmbeddingColumn,
    string RebuildWhere,
    string UpdateTrigger,
    string UpdateTriggerDdl);

/// <summary>
///     The memory bank's vec0 tables, declared once. The v21 rebuild, the dimension reconciler and
///     the every-open trigger-body guard iterate this list; a test pins it to what a fresh bank creates.
/// </summary>
public static class MemoryVecTables
{
    /// <summary>The columns every typed memory vec0 table exposes: the two partition keys and the vector.</summary>
    public static readonly IReadOnlyList<string> ExpectedColumns = ["ctx", "type", "embedding"];

    public static readonly IReadOnlyList<MemoryVecTable> All =
    [
        new("vec_entries", "embedding", "embed_state = 'embedded' AND embedding IS NOT NULL",
            "vec_entries_au", MemorySchema.VecEntriesUpdateTriggerDdl),
        new("vec_structure", "structure_embedding", "structure_embedding IS NOT NULL",
            "vec_structure_au", MemorySchema.VecStructureUpdateTriggerDdl)
    ];

    public static readonly IReadOnlyCollection<string> Names = [.. All.Select(spec => spec.Table)];
}
