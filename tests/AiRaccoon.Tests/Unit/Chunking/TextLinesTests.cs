using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     The line splitting both chunkers share: every line keeps its trailing newline, so joining
///     the lines gives back the input, and CRLF/CR endings normalise to LF first.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TextLinesTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a\n" })]
    [InlineData("a\nb", new[] { "a\n", "b" })]
    [InlineData("a\n\nb\n", new[] { "a\n", "\n", "b\n" })]
    public void Split_KeepsEachLineWithItsNewline(string text, string[] expected) =>
        TextLines.Split(text).ShouldBe(expected);

    [Theory]
    [InlineData("a\nb\n\nc")]
    [InlineData("\n\n")]
    [InlineData("no newline")]
    public void Split_JoinedBack_IsTheInput(string text) =>
        string.Concat(TextLines.Split(text)).ShouldBe(text);

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\n\rb\n", "a\n\nb\n")]
    [InlineData("plain", "plain")]
    public void NormalizeLineEndings_MakesEveryEndingLf(string text, string expected) =>
        TextLines.NormalizeLineEndings(text).ShouldBe(expected);
}
