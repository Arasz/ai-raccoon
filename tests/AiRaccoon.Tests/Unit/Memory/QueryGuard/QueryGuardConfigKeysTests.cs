using AiRaccoon.Core.Memory.QueryGuard;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard;

/// <summary>Mirrors NoiseConfigKeysTests/SweepConfigKeysTests: enabled unless explicitly "false"; shadow off unless explicitly "true".</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class QueryGuardConfigKeysTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("nonsense")]
    public void ParseEnabled_DefaultsOn(string? value) => QueryGuardConfigKeys.ParseEnabled(value).ShouldBeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void ParseEnabled_OnlyFalseTurnsItOff(string value) => QueryGuardConfigKeys.ParseEnabled(value).ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("nonsense")]
    public void ParseShadow_DefaultsOff(string? value) => QueryGuardConfigKeys.ParseShadow(value).ShouldBeFalse();

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void ParseShadow_OnlyTrueTurnsItOn(string value) => QueryGuardConfigKeys.ParseShadow(value).ShouldBeTrue();
}
