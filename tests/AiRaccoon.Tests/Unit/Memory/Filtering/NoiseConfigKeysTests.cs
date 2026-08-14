using AiRaccoon.Core.Memory.Filtering;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

/// <summary>Mirrors SweepConfigKeysTests: enabled unless explicitly "false".</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class NoiseConfigKeysTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("nonsense")]
    public void ParseEnabled_DefaultsOn(string? value) => NoiseConfigKeys.ParseEnabled(value).ShouldBeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void ParseEnabled_OnlyFalseTurnsItOff(string value) => NoiseConfigKeys.ParseEnabled(value).ShouldBeFalse();
}
