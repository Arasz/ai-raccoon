using AiRaccoon.Core.Memory.Filtering;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

/// <summary>
///     The self-learning noise subsystem's own kill switch (ADR-0039) — opposite default of
///     <see cref="NoiseConfigKeys.ParseEnabled" />: off unless the setting explicitly says "true",
///     because the research lane has not yet verified the approach works.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class NoiseLearnerConfigKeysTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("nonsense")]
    public void ParseLearnerEnabled_DefaultsOff(string? value) => NoiseConfigKeys.ParseLearnerEnabled(value).ShouldBeFalse();

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void ParseLearnerEnabled_OnlyTrueTurnsItOn(string value) => NoiseConfigKeys.ParseLearnerEnabled(value).ShouldBeTrue();

    [Theory]
    [InlineData(null, 0.12)]
    [InlineData("", 0.12)]
    [InlineData("nonsense", 0.12)]
    [InlineData("0", 0.12)]
    [InlineData("3", 0.12)]
    public void ParseLearnerClusterDistanceThreshold_InvalidOrOutOfRange_FallsBackToDefault(string? value, double expected) =>
        NoiseConfigKeys.ParseLearnerClusterDistanceThreshold(value).ShouldBe(expected);

    [Fact]
    public void ParseLearnerClusterDistanceThreshold_ValidValue_IsHonored() =>
        NoiseConfigKeys.ParseLearnerClusterDistanceThreshold("0.2").ShouldBe(0.2);
}
