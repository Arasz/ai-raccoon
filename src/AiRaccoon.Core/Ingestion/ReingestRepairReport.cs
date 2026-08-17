namespace AiRaccoon.Core.Ingestion;

/// <summary>What a reingest-repair pass found and did. A dry run (apply: false) fills it in without writing.</summary>
public sealed record ReingestRepairReport(int FilesToReingest, int RowsAffected, int ChunksToEmbed);
