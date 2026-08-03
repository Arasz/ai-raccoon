using AiRaccoon.Core.Chunking;
using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>IChunker backed by the o200k_base tokenizer and the fence-aware pure splitter.</summary>
public sealed class TokenizerChunker : IChunker
{
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
        MarkdownChunker.Split(text, maxTokens, overlayTokens, CountTokens);

    private int CountTokens(string text) => _tokenizer.CountTokens(text);
}
