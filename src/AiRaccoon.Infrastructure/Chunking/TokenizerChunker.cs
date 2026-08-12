using AiRaccoon.Core.Chunking;
using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>IChunker backed by the o200k_base tokenizer and the fence-aware pure splitter.</summary>
public sealed class TokenizerChunker : IChunker
{
    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) => MarkdownChunker.Split(text, maxTokens, overlayTokens, CountTokens);

    public static int CountTokens(string text) => Tokenizer.CountTokens(text);
}
