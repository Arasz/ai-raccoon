using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>Where <see cref="TokenBudget.SplitLength" /> cuts an over-budget text: whitespace first, then a
/// keyword-term boundary, then a hard cut; never more than the budget and never zero characters.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class TokenBudgetTests
{
    private static int CharCount(string text) => text.Length;

    [Fact]
    public void SplitLength_TextWithinBudget_IsTheWholeText() =>
        TokenBudget.SplitLength("ab cd", 10, CharCount).ShouldBe(5);

    [Fact]
    public void SplitLength_CutsJustAfterTheWhitespace() =>
        TokenBudget.SplitLength("ab cd", 4, CharCount).ShouldBe(3, "the space stays on the head, the word moves whole");

    [Fact]
    public void SplitLength_BacksOffToTheLastWhitespace_NotTheFirst() =>
        TokenBudget.SplitLength("ab cd ef gh", 10, CharCount).ShouldBe(9);

    [Fact]
    public void SplitLength_WhitespaceExactlyAtTheBudget_KeepsTheFullBudget() =>
        TokenBudget.SplitLength("abc defgh", 4, CharCount).ShouldBe(4);

    [Fact]
    public void SplitLength_LeadingWhitespaceOnly_IsNotACutPoint() =>
        TokenBudget.SplitLength("   abcdefgh", 5, CharCount).ShouldBe(5, "a head of only leading spaces would be a whitespace-only piece");

    [Fact]
    public void SplitLength_NoWhitespace_CutsAtTheLastTermBoundary() =>
        TokenBudget.SplitLength("alpha.beta.gamma", 12, CharCount).ShouldBe(11, "every keyword term stays whole: alpha. | beta. | gamma");

    [Fact]
    public void SplitLength_UnderscoreIsPartOfATerm_NotABoundary() =>
        TokenBudget.SplitLength("(call_handler_name)", 12, CharCount).ShouldBe(1, "call_handler_name is one query term; cut only before it");

    [Fact]
    public void SplitLength_WordLongerThanTheBudget_IsHardCutAtTheBudget() =>
        TokenBudget.SplitLength("abcdefghij klm", 5, CharCount).ShouldBe(5);

    [Fact]
    public void SplitLength_TextWithoutAnyBoundary_IsHardCutAtTheBudget() =>
        TokenBudget.SplitLength("記憶の銀行は検索できる", 4, CharCount).ShouldBe(4);

    [Fact]
    public void SplitLength_EvenOneCharacterIsOverBudget_StillTakesOne() =>
        TokenBudget.SplitLength("abc", 1, _ => 99).ShouldBe(1, "a split loop must always make progress");

    [Fact]
    public void SplitLength_BackedOffPrefixCountsOverBudget_FallsBackToTheLongestFit()
    {
        // A counter that is not monotonic at whitespace, as a tokenizer with whitespace-run tokens can be.
        static int Count(string text) => text == "ab " ? 99 : text.Length;

        TokenBudget.SplitLength("ab cdefgh", 6, Count).ShouldBe(6, "the backed-off prefix is re-counted and rejected");
    }

    [Fact]
    public void SplitLength_SplitLoop_EveryPieceFitsAndReassemblesTheText()
    {
        var text = "the quick brown fox jumps over the lazy dog near the riverbank";
        List<string> pieces = [];
        var remaining = text;
        while (remaining.Length > 0)
        {
            var length = TokenBudget.SplitLength(remaining, 12, CharCount);
            pieces.Add(remaining[..length]);
            remaining = remaining[length..];
        }

        pieces.ShouldBe(["the quick ", "brown fox ", "jumps over ", "the lazy ", "dog near ", "the ", "riverbank"]);
    }
}
