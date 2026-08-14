using AiRaccoon.Core.Chunking;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>Splits on blank lines — the paragraph-per-chunk fake tests reach for when a
/// multi-paragraph fixture needs to ingest as more than one chunk.</summary>
public sealed class StubChunker : IMarkdownChunker
{
    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) =>
        text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
}
