using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     BERT WordPiece <see cref="IEmbeddingTokenizer" /> — the bundled engine's family. The
///     tokenizer is built with the exact five <see cref="BertOptions" /> the bundled engine always
///     used (via <see cref="OnnxEmbeddingGenerator.CreateTokenizer" />), and
///     <see cref="EncodeToIds" /> pins the exact overload the generator called pre-WP3:
///     <c>EncodeToIds(text, addSpecialTokens, true, true)</c> (G3 golden token ids pin this).
/// </summary>
public sealed class WordPieceEmbeddingTokenizer : IEmbeddingTokenizer
{
    private readonly BertTokenizer _tokenizer;

    private WordPieceEmbeddingTokenizer(BertTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    public static WordPieceEmbeddingTokenizer Create(string vocabPath) => new(OnnxEmbeddingGenerator.CreateTokenizer(vocabPath));

    /// <summary>Content tokens, no [CLS]/[SEP] — the ADR-0036 budget unit.</summary>
    public int CountTokens(string text) => _tokenizer.CountTokens(text);

    public IReadOnlyList<int> EncodeToIds(string text, bool addSpecialTokens) => _tokenizer.EncodeToIds(text, addSpecialTokens, true, true);

    /// <summary>[CLS] + [SEP] — the two tokens <c>EncodeToIds(text, true)</c> adds.</summary>
    public int SpecialTokenReservation => 2;
}
