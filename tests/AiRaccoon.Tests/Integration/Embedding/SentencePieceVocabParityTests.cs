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
}
