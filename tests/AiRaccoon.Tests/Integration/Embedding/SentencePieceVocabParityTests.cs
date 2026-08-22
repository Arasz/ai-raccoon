using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     bge-m3 is an XLMRobertaModel (config.json vocab_size 250002, bos 0, eos 2), but its
///     sentencepiece.bpe.model carries 250000 pieces under a different numbering
///     (&lt;unk&gt;=0, &lt;s&gt;=1, &lt;/s&gt;=2). The fairseq/HF vocab adds &lt;pad&gt; and &lt;mask&gt; and
///     offsets every ordinary piece by +1. Feeding raw sentencepiece ids to the model therefore
///     reads the wrong embedding row for every token.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SentencePieceVocabParityTests : IAsyncLifetime
{
    private string _modelPath = "";

    public async ValueTask InitializeAsync() =>
        _modelPath = await TestData.EnsureSentencePieceFixtureAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private SentencePieceEmbeddingTokenizer Build() =>
        SentencePieceEmbeddingTokenizer.Create(_modelPath, new SentencePieceTokenizerOptions
        {
            AddBeginOfSentence = true,
            AddEndOfSentence = true,
            SpecialTokens = new Dictionary<string, int> { ["<s>"] = 0, ["<pad>"] = 1, ["</s>"] = 2, ["<unk>"] = 3 },
            VocabOffset = 1
        });

    /// <summary>Without the offset the ids are the sentencepiece model's own — the defect this pins.</summary>
    [Fact]
    public void WithoutTheOffset_TheIdsAreRawSentencePieceIds_WhichTheModelMisreads()
    {
        var raw = SentencePieceEmbeddingTokenizer.Create(_modelPath, new SentencePieceTokenizerOptions
        {
            AddBeginOfSentence = true,
            AddEndOfSentence = true,
            SpecialTokens = new Dictionary<string, int> { ["<s>"] = 0, ["<pad>"] = 1, ["</s>"] = 2, ["<unk>"] = 3 }
        });

        raw.EncodeToIds(",", addSpecialTokens: false)
            .ShouldContain(3, "the unmapped path emits the sentencepiece id, which is <unk> to the model");
    }

    [Fact]
    public void TheSequenceIsWrappedWithTheXlmRobertaBosAndEos()
    {
        var ids = Build().EncodeToIds("The quick brown fox jumps over the lazy dog.", addSpecialTokens: true);

        ids[0].ShouldBe(0, "bge-m3 is XLMRobertaModel: the sequence must lead with <s> = 0");
        ids[^1].ShouldBe(2, "bge-m3 is XLMRobertaModel: the sequence must close with </s> = 2");
    }

    /// <summary>
    ///     The two vocabularies differ by a constant offset on ordinary pieces. "," is
    ///     sentencepiece id 3 and xlm-roberta id 4; a tokenizer emitting 3 is off by one.
    /// </summary>
    [Fact]
    public void AnOrdinaryPieceCarriesItsXlmRobertaId_NotItsSentencePieceId()
    {
        var ids = Build().EncodeToIds(",", addSpecialTokens: false);

        ids.ShouldContain(4, "',' is xlm-roberta id 4 (sentencepiece id 3 + the fairseq offset)");
        ids.ShouldNotContain(3, "3 is <unk> in the xlm-roberta vocab the model was trained on");
    }

    /// <summary>No emitted id may exceed the model's embedding matrix (vocab_size 250002).</summary>
    [Fact]
    public void EveryEmittedIdIsInsideTheModelsVocabulary()
    {
        var ids = Build().EncodeToIds("Memory retrieval ranking evidence", addSpecialTokens: true);

        ids.ShouldAllBe(id => id >= 0 && id < 250002);
    }

    /// <summary>
    ///     WHOLE-SEQUENCE parity against the reference tokenizer, not one piece at a time. Each
    ///     expected sequence is HuggingFace <c>XLMRobertaTokenizer</c>'s own rule applied to this
    ///     exact sentencepiece model: <c>&lt;s&gt;</c>, then <c>spm_id + 1</c> for every piece
    ///     (<c>spm_id 0</c>, sentencepiece's own <c>&lt;unk&gt;</c>, maps to the model's
    ///     <c>&lt;unk&gt;</c> = 3), then <c>&lt;/s&gt;</c>. The per-piece tests above each walk one
    ///     axis; the cases here cross them — Latin text with an apostrophe and a digit-bearing
    ///     identifier, mixed scripts (CJK + accents + an em dash) in one string, and a
    ///     snake_case/camelCase identifier that the pretokenizer splits — because an off-by-one that
    ///     survives "," can still land on a rarer piece.
    /// </summary>
    [Theory]
    [InlineData("...", new[] { 0, 153, 2 })]
    [InlineData("The drain reconciles vec0 to the engine's dimension before writing.",
        new[] { 0, 581, 8167, 73, 44188, 318, 1577, 22834, 2389, 47, 70, 87907, 25, 7, 6, 91403, 8108, 32562, 5, 2 })]
    [InlineData("sqlite hybrid search fuses the FTS and vector legs with reciprocal rank fusion",
        new[] { 0, 91, 95255, 67, 113490, 33938, 57574, 90, 70, 563, 12763, 136, 173, 18770, 6049, 7, 678, 159826, 289, 30648, 119485, 2 })]
    [InlineData("Wie heißt du? Ça va — naïve café, 東京 2026.",
        new[] { 0, 4887, 61202, 115, 32, 25909, 307, 292, 24, 9392, 272, 26216, 4, 6, 22888, 387, 4046, 5, 2 })]
    [InlineData("memory_search projectId scope=all",
        new[] { 0, 98323, 454, 86250, 13452, 568, 71, 6, 70820, 1369, 5584, 2 })]
    public void TheWholeSequenceMatchesTheReferenceXlmRobertaIds(string text, int[] expected) =>
        Build().EncodeToIds(text, addSpecialTokens: true).ShouldBe(expected,
            $"'{text}' must tokenize to the ids XLMRobertaTokenizer produces for this sentencepiece model");
}
