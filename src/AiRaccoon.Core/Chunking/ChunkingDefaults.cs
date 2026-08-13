namespace AiRaccoon.Core.Chunking;

/// <summary>Shared chunking budget defaults (see docs/explanation/architecture.md §Chunk bounds).</summary>
public static class ChunkingDefaults
{
    /// <summary>Tail-of-previous-chunk overlap for markdown/text chunking.</summary>
    public const int OverlayTokens = 48;
}
