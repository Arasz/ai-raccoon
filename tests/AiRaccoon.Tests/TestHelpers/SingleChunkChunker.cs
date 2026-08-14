using AiRaccoon.Core.Chunking;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>Returns the whole text as a single chunk — for tests where chunk boundaries don't matter.</summary>
public sealed class SingleChunkChunker : IMarkdownChunker
{
    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) => [text];
}
