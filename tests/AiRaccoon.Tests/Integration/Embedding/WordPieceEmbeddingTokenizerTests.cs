using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP3 tokenizer seam (D5/D9): the wordpiece implementation must reproduce the bundled
///     engine's exact tokenization — the pinned <c>BertTokenizer.EncodeToIds(text, true, true, true)</c>
///     overload the generator used before the refactor (G3 pins this via the golden token ids too).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WordPieceEmbeddingTokenizerTests
{
    private static readonly string[] SampleTexts =
    [
        "Why could plain content-only vector search not answer section-targeted questions?",
        "这是一个用于测试的中文段落,包含很多汉字。",
        "Memory retrieval ranking evidence 🦝 with an emoji and a URL https://example.com/a/b?q=1",
        "  leading and trailing whitespace  ",
        "hash-list\nfragment\nwith\nnewlines"
    ];

    [Fact]
    public void EncodeToIds_WithSpecialTokens_MatchesThePinnedBertOverload()
    {
        var vocab = BundledModel.ResolveVocabPath();
        var tokenizer = WordPieceEmbeddingTokenizer.Create(vocab);
        var reference = OnnxEmbeddingGenerator.CreateTokenizer(vocab);

        foreach (var text in SampleTexts)
        {
            tokenizer.EncodeToIds(text, addSpecialTokens: true)
                .ShouldBe(reference.EncodeToIds(text, true, true, true),
                    $"EncodeToIds(text, true) must equal the pinned EncodeToIds(text, true, true, true) for '{text}'");
        }
    }

    [Fact]
    public void EncodeToIds_WithoutSpecialTokens_MatchesThePinnedOverloadWithSpecialTokensOff()
    {
        var vocab = BundledModel.ResolveVocabPath();
        var tokenizer = WordPieceEmbeddingTokenizer.Create(vocab);
        var reference = OnnxEmbeddingGenerator.CreateTokenizer(vocab);

        foreach (var text in SampleTexts)
        {
            tokenizer.EncodeToIds(text, addSpecialTokens: false)
                .ShouldBe(reference.EncodeToIds(text, false, true, true),
                    $"EncodeToIds(text, false) must equal the pinned overload with addSpecialTokens=false for '{text}'");
        }
    }

    [Fact]
    public void CountTokens_MatchesBertTokenizerCountTokens()
    {
        var vocab = BundledModel.ResolveVocabPath();
        var tokenizer = WordPieceEmbeddingTokenizer.Create(vocab);
        var reference = OnnxEmbeddingGenerator.CreateTokenizer(vocab);

        foreach (var text in SampleTexts)
        {
            tokenizer.CountTokens(text).ShouldBe(reference.CountTokens(text), $"CountTokens mismatch for '{text}'");
        }
    }

    [Fact]
    public void SpecialTokenReservation_IsTheBertClsSepPair()
    {
        WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()).SpecialTokenReservation.ShouldBe(2);
    }

    [Fact]
    public void EncodeToIds_AddsExactlyTheReservedSpecialTokens_AtWindowEdge()
    {
        // A text whose content is exactly at the bundled window: CLS + 254 content + SEP = 256 ids,
        // i.e. the special-token reservation is exactly what the ADR-0036 budget math assumes.
        var vocab = BundledModel.ResolveVocabPath();
        var tokenizer = WordPieceEmbeddingTokenizer.Create(vocab);
        var reference = OnnxEmbeddingGenerator.CreateTokenizer(vocab);

        var filler = string.Join(' ', Enumerable.Repeat(
            "how does the retrieval pipeline weigh full text against vectors when the corpus is large", 40)); // > 254 content tokens
        var atLimit = TokenBudget.Trim(filler, OnnxEmbeddingGenerator.MaxContentTokens,
            new TokenCount(text => reference.CountTokens(text)));

        tokenizer.CountTokens(atLimit).ShouldBe(OnnxEmbeddingGenerator.MaxContentTokens);
        tokenizer.EncodeToIds(atLimit, addSpecialTokens: true).Count.ShouldBe(OnnxEmbeddingGenerator.MaxContentTokens + 2);
    }
}
