using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Construction options for the sentencepiece family, mirroring the D1 manifest
/// <c>tokenizer.options</c> shape (addBeginOfSentence/addEndOfSentence + numeric special-token map).</summary>
public sealed record SentencePieceTokenizerOptions(
    bool AddBeginOfSentence = true,
    bool AddEndOfSentence = true,
    IReadOnlyDictionary<string, int>? SpecialTokens = null,
    int VocabOffset = 0);

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
    private readonly int _vocabOffset;
    private readonly int _bos;
    private readonly int _eos;
    private readonly int _unk;

    private SentencePieceEmbeddingTokenizer(SentencePieceTokenizer tokenizer, SentencePieceTokenizerOptions options)
    {
        _tokenizer = tokenizer;
        _addBeginOfSentence = options.AddBeginOfSentence;
        _addEndOfSentence = options.AddEndOfSentence;
        _vocabOffset = options.VocabOffset;
        var specials = options.SpecialTokens;
        _bos = Special(specials, "<s>", tokenizer.BeginningOfSentenceId);
        _eos = Special(specials, "</s>", tokenizer.EndOfSentenceId);
        _unk = Special(specials, "<unk>", tokenizer.UnknownId);
    }

    private static int Special(IReadOnlyDictionary<string, int>? specials, string token, int fallback) => specials is not null && specials.TryGetValue(token, out var id) ? id : fallback;

    /// <summary>
    ///     Maps a sentencepiece id onto the model's vocabulary. Fairseq-derived models (xlm-roberta,
    ///     and so bge-m3) prepend their own specials and shift every ordinary piece by
    ///     <c>VocabOffset</c>; the three control pieces move to the ids the manifest declares. With
    ///     the default offset of 0 this is the identity, which is what a plain sentencepiece model
    ///     (T5, LLaMA) needs.
    /// </summary>
    private int ToModelVocab(int spmId)
    {
        if (_vocabOffset == 0)
        {
            return spmId;
        }

        if (spmId == _tokenizer.BeginningOfSentenceId)
        {
            return _bos;
        }

        if (spmId == _tokenizer.EndOfSentenceId)
        {
            return _eos;
        }

        if (spmId == _tokenizer.UnknownId)
        {
            return _unk;
        }

        return spmId + _vocabOffset;
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

    public IReadOnlyList<int> EncodeToIds(string text, bool addSpecialTokens)
    {
        var ids = addSpecialTokens
            ? _tokenizer.EncodeToIds(text, _addBeginOfSentence, _addEndOfSentence, true, true)
            : _tokenizer.EncodeToIds(text, false, false, true, true);
        if (_vocabOffset == 0)
        {
            return ids;
        }

        var mapped = new int[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            mapped[i] = ToModelVocab(ids[i]);
        }

        return mapped;
    }

    /// <summary>&lt;s&gt; + &lt;/s&gt; — the two tokens the engine adds when both flags are on.</summary>
    public int SpecialTokenReservation => 2;
}
