using AiRaccoon.Core.Decisions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Decisions;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DecisionTextTests
{
    [Fact]
    public void Required_measures_the_trimmed_text_against_the_maximum() =>
        Should.NotThrow(() => DecisionText.Required("  " + new string('a', 200) + " ", 200, "title"));

    [Fact]
    public void Required_refuses_trimmed_text_over_the_maximum() =>
        Should.Throw<ArgumentException>(() => DecisionText.Required(" " + new string('a', 201), 200, "title"));

    [Theory]
    [InlineData(0x202E)]
    [InlineData(0x200B)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x00AD)]
    [InlineData(0x000A)]
    public void Identifier_refuses_invisible_and_separator_characters(int codePoint) =>
        Should.Throw<ArgumentException>(() => DecisionText.Identifier($"task{(char)codePoint}1", "taskId"));

    [Theory]
    [InlineData(" task-1")]
    [InlineData("task-1 ")]
    public void Identifier_refuses_leading_or_trailing_whitespace(string value) =>
        Should.Throw<ArgumentException>(() => DecisionText.Identifier(value, "taskId"));

    [Theory]
    [InlineData("task-1")]
    [InlineData("01a102bb-6a2f-4f3c-9d55-0f6c1b9aec2c")]
    [InlineData("Rafal A")]
    [InlineData("zadanie-ł")]
    public void Identifier_accepts_visible_text(string value) =>
        Should.NotThrow(() => DecisionText.Identifier(value, "taskId"));
}
