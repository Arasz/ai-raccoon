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

    // Structural detector (docs/adr/0041): default off — the write-path equivalent
    // (noise.learner.enabled.global) is off by the same convention, and the record explicitly
    // recommends "default off, on both paths."
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("nonsense")]
    public void ParseStructuralEnabled_DefaultsOff(string? value) =>
        QueryGuardConfigKeys.ParseStructuralEnabled(value).ShouldBeFalse();

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void ParseStructuralEnabled_OnlyTrueTurnsItOn(string value) =>
        QueryGuardConfigKeys.ParseStructuralEnabled(value).ShouldBeTrue();

    // Threshold default: scripts/train-structural-noise-model.py, 5-fold out-of-fold calibration
    // at the 2% target on the training split of gen2/queries.jsonl (docs/adr/0041 §Measurement) —
    // not a magic number, and overridable per-deployment via this same setting.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void ParseStructuralThreshold_WithNoUsableValue_ReturnsTheShippedDefault(string? value) =>
        QueryGuardConfigKeys.ParseStructuralThreshold(value).ShouldBe(QueryGuardConfigKeys.DefaultStructuralThreshold);

    [Fact]
    public void ParseStructuralThreshold_WithAnOverride_UsesIt()
    {
        QueryGuardConfigKeys.ParseStructuralThreshold("0.75").ShouldBe(0.75, 1e-9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void ParseStructuralTargetFpr_WithNoUsableValue_ReturnsTheDefault(string? value) =>
        QueryGuardConfigKeys.ParseStructuralTargetFpr(value).ShouldBe(QueryGuardConfigKeys.DefaultStructuralTargetFpr);

    [Fact]
    public void ParseStructuralTargetFpr_WithAnOverride_UsesIt()
    {
        QueryGuardConfigKeys.ParseStructuralTargetFpr("0.01").ShouldBe(0.01, 1e-9);
    }
}
