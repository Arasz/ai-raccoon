using AiRaccoon.Infrastructure.Sync;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sync;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class CloudEtagTests
{
    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("abc", "abc")]
    [InlineData("\"\"", "")]
    public void Strip_RemovesSurroundingQuotes(string raw, string expected) => CloudEtag.Strip(raw).ShouldBe(expected);

    [Fact]
    public void Strip_Null_ReturnsNull() => CloudEtag.Strip(null).ShouldBeNull();
}
