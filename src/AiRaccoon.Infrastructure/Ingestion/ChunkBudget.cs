using AiRaccoon.Core.Chunking;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>Resolved chunk sizing shared by the backfill and position-scan repair passes: the token ceiling, overlap, and the counter that enforces them.</summary>
public sealed record ChunkBudget(int MaxTokens, int OverlayTokens, TokenCount CountTokens);
