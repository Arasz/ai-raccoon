namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>A stored chunk row's id and content hash, as fed into <see cref="ChunkPositionScanner.Scan" />.</summary>
public readonly record struct StoredChunk(long Id, string Hash);
