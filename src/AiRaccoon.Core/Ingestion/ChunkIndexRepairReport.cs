namespace AiRaccoon.Core.Ingestion;

/// <summary>What a chunk-index-repair pass found and did. A dry run (apply: false) fills it in without writing.</summary>
public sealed record ChunkIndexRepairReport(int GroupsExamined, int RowsRepositioned, int RowsSetToUnknown);
