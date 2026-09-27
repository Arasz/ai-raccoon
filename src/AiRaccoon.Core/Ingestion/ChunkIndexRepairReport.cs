namespace AiRaccoon.Core.Ingestion;

/// <summary>What a chunk-index-repair pass found and did; a dry run (apply: false) fills it in without writing.
/// <see cref="RowsRetotalled" /> counts rows whose position was right but whose total_chunks was not.</summary>
public sealed record ChunkIndexRepairReport(int GroupsExamined, int RowsRepositioned, int RowsSetToUnknown, int RowsRetotalled);
