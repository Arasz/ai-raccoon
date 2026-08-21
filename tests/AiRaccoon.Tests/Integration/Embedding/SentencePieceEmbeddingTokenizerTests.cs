using AiRaccoon.Infrastructure.Embedding;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP3 sentencepiece family (D5): xlm-roberta-style bos/eos double reservation and the
///     manifest special-token map. The fixture is the real sentencepiece.bpe.model from
///     BAAI/bge-m3's onnx/ export, pinned by sha (Resources/tokenizers/manifest.json).
///     Parity against the HF fast tokenizer is out of WP3 scope (engineer doc A2 — WP5/lane A).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SentencePieceEmbeddingTokenizerTests : IAsyncLifetime
{
    private string _modelPath = "";

    public async ValueTask InitializeAsync() =>
        _modelPath = await TestData.EnsureSentencePieceFixtureAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly IReadOnlyDictionary<string, int> XlmRobertaSpecialTokens =
        new Dictionary<string, int> { ["<s>"] = 0, ["<pad>"] = 1, ["</s>"] = 2, ["<unk>"] = 3 };

    private SentencePieceEmbeddingTokenizer Build(bool addBeginOfSentence = true, bool addEndOfSentence = true) =>
        SentencePieceEmbeddingTokenizer.Create(_modelPath, new SentencePieceTokenizerOptions
        {
            AddBeginOfSentence = addBeginOfSentence,
            AddEndOfSentence = addEndOfSentence,
            SpecialTokens = XlmRobertaSpecialTokens
        });

    [Fact]
    public void Create_LoadsThePinnedModel_AndCountsTokens()
    {
        var tokenizer = Build();
        tokenizer.CountTokens("The quick brown fox jumps over the lazy dog.").ShouldBeGreaterThan(0);
    }

    [Fact]
    public void EncodeToIds_WithSpecialTokens_AddsBosAndEos()
    {
        var tokenizer = Build();
        var ids = tokenizer.EncodeToIds("Memory retrieval ranking evidence", addSpecialTokens: true);
        var without = tokenizer.EncodeToIds("Memory retrieval ranking evidence", addSpecialTokens: false);

        // The fixture model's own bos/eos ids (probed from ML.Tokenizers: bos=1, eos=2 for the
        // bge-m3 onnx sentencepiece model — NOT the xlm-roberta vocab ids, which differ). The
        // contract being pinned is "exactly two extra ids at the edges", whatever their values.
        using var stream = File.OpenRead(_modelPath);
        var raw = SentencePieceTokenizer.Create(stream, true, true, XlmRobertaSpecialTokens);
        ids[0].ShouldBe(raw.BeginningOfSentenceId, "xlm-roberta sentencepiece must lead with the model's bos id");
        ids[^1].ShouldBe(raw.EndOfSentenceId, "xlm-roberta sentencepiece must close with the model's eos id");
        ids.Count.ShouldBe(without.Count + 2);
    }

    [Fact]
    public void EncodeToIds_WithoutSpecialTokens_OmitsBosAndEos()
    {
        var tokenizer = Build();
        var with = tokenizer.EncodeToIds("Memory retrieval ranking evidence", addSpecialTokens: true);
        var without = tokenizer.EncodeToIds("Memory retrieval ranking evidence", addSpecialTokens: false);

        without.Count.ShouldBe(with.Count - 2);
        without.ShouldNotContain(0);
        without.ShouldNotContain(2);
    }

    [Fact]
    public void SpecialTokenReservation_IsTheBosEosPair()
    {
        Build().SpecialTokenReservation.ShouldBe(2);
    }

    [Fact]
    public void CountTokens_IsTheContentCount_WithoutSpecialTokens()
    {
        var tokenizer = Build();
        const string text = "How does the retrieval pipeline weigh full text against vectors?";
        tokenizer.CountTokens(text)
            .ShouldBe(tokenizer.EncodeToIds(text, addSpecialTokens: true).Count - tokenizer.SpecialTokenReservation);
    }

    [Fact]
    public void EncodeToIds_IsDeterministic_AcrossCalls()
    {
        var tokenizer = Build();
        const string text = "A second call must produce the same ids for the same text.";
        tokenizer.EncodeToIds(text, addSpecialTokens: true)
            .ShouldBe(tokenizer.EncodeToIds(text, addSpecialTokens: true));
    }
}
