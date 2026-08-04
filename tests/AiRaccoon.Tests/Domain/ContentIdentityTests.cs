using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Domain;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ContentIdentityTests
{
    [Fact]
    public void Hash_IsDeterministic_ForTheSamePathAndValue() =>
        ContentHash.Of("docs/note.md", "project knowledge").ShouldBe(
            ContentHash.Of("docs/note.md", "project knowledge"));

    [Fact]
    public void Hash_DiffersWhenThePathChanges_AndTheValueStays() =>
        ContentHash.Of("a.md", "same content").ShouldNotBe(
            ContentHash.Of("b.md", "same content"));

    [Fact]
    public void Hash_DiffersWhenTheValueChanges_AndThePathStays() =>
        ContentHash.Of("a.md", "first").ShouldNotBe(
            ContentHash.Of("a.md", "second"));

    [Fact]
    public void Hash_IsSha256OverUtf8PathConcatenatedWithUtf8Value()
    {
        var path = "docs/nöte.md";
        var value = "wörld knowledge";

        var expected = Convert.ToHexStringLower(
            SHA256.HashData([.. Encoding.UTF8.GetBytes(path), .. Encoding.UTF8.GetBytes(value)]));

        ContentHash.Of(path, value).ShouldBe(expected);
        ContentHash.Of(path, value).ShouldBe("148354579df887013241d8b8106f01967896b4ba3f3d58987c6fc26aa5674296");
    }

    [Fact]
    public void Hash_ConcatenatesWithoutSeparator_PinningTheByteBoundaryContract() =>
        // UTF8(path) || UTF8(value) with no separator: ("a", "bc") and ("ab", "c") share bytes.
        ContentHash.Of("a", "bc").ShouldBe(ContentHash.Of("ab", "c"));

    [Fact]
    public void Hash_MatchesTheKnownAnswer_ForAsciiPathAndValue() =>
        ContentHash.Of("a", "b").ShouldBe("fb8e20fc2e4c3f248c60c39bd652f3c1347298bb977b8b4d5903b85055620603");

    [Fact]
    public void Hash_WithNullPath_Throws() => Should.Throw<ArgumentNullException>(() => ContentHash.Of(null!, "v"));

    [Fact]
    public void Hash_WithNullValue_Throws() => Should.Throw<ArgumentNullException>(() => ContentHash.Of("p", null!));
}
