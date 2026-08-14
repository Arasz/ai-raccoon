using AiRaccoon.Core.Degradation;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sweep;

/// <summary>The sweep knobs parse with defaults: enabled unless explicitly "false", interval clamped 1..8760 hours.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SweepConfigKeysTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("nonsense")]
    public void ParseEnabled_DefaultsOn(string? value) => SweepConfigKeys.ParseEnabled(value).ShouldBeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void ParseEnabled_OnlyFalseTurnsItOff(string value) => SweepConfigKeys.ParseEnabled(value).ShouldBeFalse();

    [Theory]
    [InlineData(null, SweepConfigKeys.DefaultIntervalHours)]
    [InlineData("not-a-number", SweepConfigKeys.DefaultIntervalHours)]
    [InlineData("6", 6)]
    [InlineData("0", SweepConfigKeys.MinIntervalHours)]
    [InlineData("-5", SweepConfigKeys.MinIntervalHours)]
    [InlineData("100000", SweepConfigKeys.MaxIntervalHours)]
    public void ParseIntervalHours_ClampsToTheSupportedRange(string? value, int expected) => SweepConfigKeys.ParseIntervalHours(value).ShouldBe(expected);
}
