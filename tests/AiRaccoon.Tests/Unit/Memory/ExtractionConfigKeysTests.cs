using System.Globalization;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ExtractionConfigKeysTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("4.1")]
    [InlineData("99")]
    public void ParseAutoPromoteThreshold_DisabledOrInvalid_ReturnsNull(string? raw) =>
        ExtractionConfigKeys.ParseAutoPromoteThreshold(raw).ShouldBeNull();

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("3.5", 3.5)]
    [InlineData("4", 4.0)]
    public void ParseAutoPromoteThreshold_ValidScore_ReturnsTheValue(string raw, double expected) =>
        ExtractionConfigKeys.ParseAutoPromoteThreshold(raw).ShouldBe(expected);

    [Fact]
    public void FormatAutoPromoteThreshold_Null_ReadsAsOff() =>
        ExtractionConfigKeys.FormatAutoPromoteThreshold(null).ShouldBe("off");

    [Fact]
    public void FormatAutoPromoteThreshold_Value_RoundTripsThroughParse()
    {
        var formatted = ExtractionConfigKeys.FormatAutoPromoteThreshold(3.5);

        ExtractionConfigKeys.ParseAutoPromoteThreshold(formatted).ShouldBe(3.5);
        double.Parse(formatted, CultureInfo.InvariantCulture).ShouldBe(3.5);
    }
}
