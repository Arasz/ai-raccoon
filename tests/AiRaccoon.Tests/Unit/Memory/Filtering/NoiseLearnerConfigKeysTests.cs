using AiRaccoon.Core.Memory.Filtering;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

/// <summary>
///     The self-learning noise subsystem's own kill switches (ADR-0039) — opposite default of
///     <see cref="NoiseConfigKeys.ParseEnabled" />: off unless the setting explicitly says "true".
///     No similarity-threshold setting exists here: a distance cutoff is scoring logic, and no
///     detector has been validated on held-out data yet (see docs/adr/0039).
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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("nonsense")]
    public void ParseLearnerShadowEnabled_DefaultsOff(string? value) => NoiseConfigKeys.ParseLearnerShadowEnabled(value).ShouldBeFalse();

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void ParseLearnerShadowEnabled_OnlyTrueTurnsItOn(string value) => NoiseConfigKeys.ParseLearnerShadowEnabled(value).ShouldBeTrue();
}
