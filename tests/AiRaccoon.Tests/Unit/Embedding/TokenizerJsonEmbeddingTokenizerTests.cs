using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Fast, hand-written-fixture pins for <see cref="TokenizerJsonEmbeddingTokenizer" />'s core
///     mechanics — byte-level mapping, merge ranking, template/roberta/sequence post-processor
///     specials, sentencepiece-style byte fallback and added-token protection. Expected ids in
///     every test were produced by loading the same fixture through HF `tokenizers` 0.22.2 and are
///     not hand-guessed. The 7-model, 70-case real-model parity gate lives in
///     <c>TokenizerJsonParityTests</c> (Nightly).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class TokenizerJsonEmbeddingTokenizerTests
{
    private static string Fixture(string name) => TestData.RepoFile($"tests/AiRaccoon.Tests/TestData/Tokenizers/{name}");

    [Fact]
    public void ByteLevelBpe_MergesAdjacentSymbolsByRank_AndMapsBytesToTheGpt2Alphabet()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-bytelevel.json"));

        // "ab" merges (rank 0); the space before "c" byte-maps to 'Ġ' and merges with "c" (rank 1).
        tokenizer.EncodeToIds("ab c", addSpecialTokens: false).ShouldBe([6, 7]);
    }

    [Fact]
    public void ByteLevelBpe_LeavesAnUnmergedPairAsSeparateSymbols()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-bytelevel.json"));

        // "Ġ"+"b" has no merge rule, so the byte-mapped space and 'b' stay two distinct tokens.
        tokenizer.EncodeToIds("a b c", addSpecialTokens: false).ShouldBe([0, 5, 1, 7]);
    }

    [Fact]
    public void TemplateProcessing_WrapsWithClsAndSep_AndReservesTwoTokens()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-bytelevel.json"));

        tokenizer.EncodeToIds("ab c", addSpecialTokens: true).ShouldBe([8, 6, 7, 9]);
        tokenizer.SpecialTokenReservation.ShouldBe(2);
    }

    [Fact]
    public void CountTokens_CountsContentTokensOnly_NeverTheTemplateSpecials()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-bytelevel.json"));

        tokenizer.CountTokens("ab c").ShouldBe(2);
        tokenizer.CountTokens(string.Empty).ShouldBe(0);
    }

    [Fact]
    public void AddedToken_IsNeverSplitByMerging_EvenWhenItsLettersWouldOtherwiseByteLevelSplit()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-bytelevel.json"));

        // Without added-token protection "FOO" byte-level-splits into 'F'/'O'/'O' (ids 3,4,4);
        // protected, it is the single added-token id 10.
        tokenizer.EncodeToIds("FOO", addSpecialTokens: false).ShouldBe([10]);
        tokenizer.EncodeToIds("ab FOO c", addSpecialTokens: false).ShouldBe([6, 5, 10, 7]);
    }

    [Fact]
    public void RobertaProcessing_WrapsWithClsAndSepFromItsOwnFields()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-roberta.json"));

        tokenizer.EncodeToIds("ab c", addSpecialTokens: true).ShouldBe([8, 4, 5, 9]);
        tokenizer.EncodeToIds(string.Empty, addSpecialTokens: true).ShouldBe([8, 9]);
        tokenizer.SpecialTokenReservation.ShouldBe(2);
    }

    [Fact]
    public void SequencePreTokenizer_SplitsByRegexThenByteLevelMaps_AndAppendsOnlyOneSpecial()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-sequence-eosonly.json"));

        // Qwen3 shape: pre_tokenizer Sequence(Split regex, ByteLevel); post_processor only
        // appends the eos-equivalent special — no leading token at all.
        tokenizer.EncodeToIds("ab c", addSpecialTokens: true).ShouldBe([4, 3, 2, 8]);
        tokenizer.EncodeToIds(string.Empty, addSpecialTokens: true).ShouldBe([8]);
        tokenizer.SpecialTokenReservation.ShouldBe(1);
    }

    [Fact]
    public void SentencePieceByteFallback_MergesKnownChars_AndReplacesSpaceWithTheMetaspaceMarker()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-spm-bytefallback.json"));

        // "H"+"i" merges (rank 0, vocab entry "Hi"); the literal space normalizes to '▁' and the
        // remaining letters have no merge rule, so each stays its own token.
        tokenizer.EncodeToIds("Hi world", addSpecialTokens: false).ShouldBe([15, 6, 7, 8, 9, 10, 11]);
        tokenizer.EncodeToIds("Hi world", addSpecialTokens: true).ShouldBe([2, 15, 6, 7, 8, 9, 10, 11, 1]);
    }

    [Fact]
    public void SentencePieceByteFallback_UnknownCharacter_ExpandsToItsUtf8BytesAsHexTokens()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-spm-bytefallback.json"));

        // "€" (U+20AC) has no vocab entry of its own; byte_fallback=true expands its 3-byte UTF-8
        // encoding (E2 82 AC) into the three <0xNN> tokens the fixture's vocab declares.
        tokenizer.EncodeToIds("H€i", addSpecialTokens: false).ShouldBe([4, 12, 13, 14, 5]);
    }

    [Fact]
    public void SentencePieceByteFallback_EmptyText_HasNoContentTokens()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-spm-bytefallback.json"));

        tokenizer.EncodeToIds(string.Empty, addSpecialTokens: false).ShouldBeEmpty();
        tokenizer.EncodeToIds(string.Empty, addSpecialTokens: true).ShouldBe([2, 1]);
        tokenizer.SpecialTokenReservation.ShouldBe(2);
    }

    [Fact]
    public void WordPiece_LowercasesAndGreedilyMatchesLongestSubwordsWithTheContinuationPrefix()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-wordpiece.json"));

        // BertNormalizer lowercases; BertPreTokenizer isolates "!" as its own word, which has no
        // vocab entry without the "##" prefix, so it falls back to [UNK] rather than "##!".
        tokenizer.EncodeToIds("Hello world!", addSpecialTokens: false).ShouldBe([4, 5, 6, 1]);
        tokenizer.EncodeToIds("Hello world!", addSpecialTokens: true).ShouldBe([2, 4, 5, 6, 1, 3]);
    }

    [Fact]
    public void WordPiece_WholeWordNotInVocabulary_BecomesASingleUnknownToken()
    {
        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(Fixture("tiny-wordpiece.json"));

        tokenizer.EncodeToIds("cafe zzz", addSpecialTokens: false).ShouldBe([8, 9, 1]);
    }
}
