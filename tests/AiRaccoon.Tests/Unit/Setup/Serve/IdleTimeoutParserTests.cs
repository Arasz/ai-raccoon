using AiRaccoon.Hosting.Node;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     IdleTimeoutParser: the serve --idle-timeout span grammar (90s/30m/4h/1d; 0 disables the watchdog).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class IdleTimeoutParserTests
{
    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("4h", 14400)]
    [InlineData("1d", 86400)]
    public void TryParse_ValidSpan_ReturnsExpectedTimeSpan(string value, long totalSeconds)
    {
        IdleTimeoutParser.TryParse(value, out var timeout).ShouldBeTrue();

        timeout.ShouldBe(TimeSpan.FromSeconds(totalSeconds));
    }

    [Fact]
    public void TryParse_Zero_ReturnsZero()
    {
        IdleTimeoutParser.TryParse("0", out var timeout).ShouldBeTrue();

        timeout.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("4x")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1.5h")]
    [InlineData("0s")]
    [InlineData("999999999d")]
    [InlineData(null)]
    public void TryParse_InvalidValue_ReturnsFalse(string? value)
    {
        IdleTimeoutParser.TryParse(value, out _).ShouldBeFalse();
    }
}
