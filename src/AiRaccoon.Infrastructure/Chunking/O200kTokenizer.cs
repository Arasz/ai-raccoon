using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>o200k_base tokenizer backing the TokenCount delegate the chunkers consume.</summary>
public sealed class O200kTokenizer
{
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public int CountTokens(string text) => _tokenizer.CountTokens(text);
}
