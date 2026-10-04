using System.Globalization;
using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     One pure renderer stands between caller-supplied ids/names and every refusal echo: unsafe
///     runes become <c>\uXXXX</c> escapes and the result is capped.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdTextTests
{
    [Theory]
    [InlineData("bad\u001bname", "\\u001B")]
    [InlineData("bad\r\nname", "\\u000D\\u000A")]
    [InlineData("bad\u202ename", "\\u202E")]
    [InlineData("bad\uE000name", "\\uE000")]
    public void UnsafeRunes_AreEscaped(string value, string expectedEscape)
    {
        var printable = ProjectIdText.Printable(value);

        printable.ShouldContain(expectedEscape);
        printable.Any(IsUnsafe).ShouldBeFalse($"a raw unsafe rune survived: {printable}");
    }

    /// <summary>A lone surrogate cannot ride through an InlineData row — xunit's case enumeration
    /// replaces it — so the value is built in the body to keep the row honest.</summary>
    [Fact]
    public void LoneSurrogate_IsEscaped()
    {
        var printable = ProjectIdText.Printable("bad" + '\ud800' + "name");

        printable.ShouldContain("\\uD800");
        printable.Any(IsUnsafe).ShouldBeFalse($"a raw unsafe rune survived: {printable}");
    }

    [Fact]
    public void APlainId_PassesThroughUnchanged()
    {
        ProjectIdText.Printable("ai-badger").ShouldBe("ai-badger");
    }

    [Fact]
    public void LongerThanTheCap_IsTruncatedToTheCap()
    {
        var printable = ProjectIdText.Printable(new string('a', 500));

        printable.Length.ShouldBe(200);
    }

    [Fact]
    public void Truncation_NeverSplitsAnEscape()
    {
        var printable = ProjectIdText.Printable("ab\u001bcd", maxLength: 5);

        printable.ShouldBe("ab");
    }

    private static bool IsUnsafe(char ch) =>
        CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse;
}
