using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Construction options for the sentencepiece family, mirroring the D1 manifest
/// <c>tokenizer.options</c> shape (addBeginOfSentence/addEndOfSentence + numeric special-token map).</summary>
public sealed record SentencePieceTokenizerOptions(
    bool AddBeginOfSentence = true,
    bool AddEndOfSentence = true,
    IReadOnlyDictionary<string, int>? SpecialTokens = null);

/// <summary>
///     SentencePiece <see cref="IEmbeddingTokenizer" /> (xlm-roberta family — bge-m3's tokenizer,
///     docs/work/2026-08-21-arbitrary-embedding-models-plan.md D5). Wraps
///     <c>Microsoft.ML.Tokenizers.SentencePieceTokenizer.Create(stream, addBeginOfSentence,
///     addEndOfSentence, specialTokens)</c> with the manifest's options. Tokenizer parity against the
///     HF fast tokenizer is out of WP3 scope (engineer doc A2 — pinned by WP5/lane A fixtures).
/// </summary>
public sealed class SentencePieceEmbeddingTokenizer : IEmbeddingTokenizer
{
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly bool _addBeginOfSentence;
    private readonly bool _addEndOfSentence;

    private SentencePieceEmbeddingTokenizer(SentencePieceTokenizer tokenizer, SentencePieceTokenizerOptions options)
    {
        _tokenizer = tokenizer;
        _addBeginOfSentence = options.AddBeginOfSentence;
        _addEndOfSentence = options.AddEndOfSentence;
    }

    public static SentencePieceEmbeddingTokenizer Create(string modelPath, SentencePieceTokenizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var stream = File.OpenRead(modelPath);
        var tokenizer = SentencePieceTokenizer.Create(
            stream, options.AddBeginOfSentence, options.AddEndOfSentence, options.SpecialTokens);
        return new SentencePieceEmbeddingTokenizer(tokenizer, options);
    }

    /// <summary>
    ///     Content tokens without the &lt;s&gt;/&lt;/s&gt; the engine adds at embed time. Note that
    ///     ML.Tokenizers' SentencePieceTokenizer.CountTokens(text) counts WITH the configured
    ///     bos/eos, so this counts with both flags off — the ADR-0036 budget unit.
    /// </summary>
    public int CountTokens(string text) => _tokenizer.CountTokens(text, false, false, true, true);

    public IReadOnlyList<int> EncodeToIds(string text, bool addSpecialTokens) =>
        addSpecialTokens
            ? _tokenizer.EncodeToIds(text, _addBeginOfSentence, _addEndOfSentence, true, true)
            : _tokenizer.EncodeToIds(text, false, false, true, true);

    /// <summary>&lt;s&gt; + &lt;/s&gt; — the two tokens the engine adds when both flags are on.</summary>
    public int SpecialTokenReservation => 2;
}
