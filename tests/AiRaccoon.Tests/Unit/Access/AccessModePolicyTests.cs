using AiRaccoon.Core.Access;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Access;

/// <summary>Pure resolution policy for ro / rw / full access modes (FR-NM-2; see docs/work/features-native-memory/native-memory.feature).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class AccessModePolicyTests
{
    [Fact]
    public void Resolve_PerProjectOverride_BeatsGlobal()
    {
        AccessModePolicy.Resolve(AccessMode.Ro, AccessMode.Full).ShouldBe(AccessMode.Full);
        AccessModePolicy.Resolve(AccessMode.Full, AccessMode.Ro).ShouldBe(AccessMode.Ro);
    }

    [Fact]
    public void Resolve_NoPerProject_UsesGlobalDefault()
    {
        AccessModePolicy.Resolve(AccessMode.Ro, null).ShouldBe(AccessMode.Ro);
        AccessModePolicy.Resolve(AccessMode.Full, null).ShouldBe(AccessMode.Full);
    }

    [Fact]
    public void Resolve_NothingSet_DefaultsToRw() => AccessModePolicy.Resolve(null, null).ShouldBe(AccessMode.Rw);

    [Theory]
    [InlineData(AccessMode.Ro)]
    [InlineData(AccessMode.Rw)]
    [InlineData(AccessMode.Full)]
    public void Allows_Read_IsAllowedInEveryMode(AccessMode mode) => AccessModePolicy.Allows(mode, AccessRequirement.Read).ShouldBeTrue();

    [Theory]
    [InlineData(AccessMode.Ro, false)]
    [InlineData(AccessMode.Rw, true)]
    [InlineData(AccessMode.Full, true)]
    public void Allows_Write_RequiresRwOrFull(AccessMode mode, bool allowed) => AccessModePolicy.Allows(mode, AccessRequirement.Write).ShouldBe(allowed);

    [Theory]
    [InlineData(AccessMode.Ro, false)]
    [InlineData(AccessMode.Rw, false)]
    [InlineData(AccessMode.Full, true)]
    public void Allows_Destructive_RequiresFull(AccessMode mode, bool allowed) => AccessModePolicy.Allows(mode, AccessRequirement.Destructive).ShouldBe(allowed);

    [Fact]
    public void RequiredFor_WriteIsRw_AndDestructiveIsFull()
    {
        AccessModePolicy.RequiredFor(AccessRequirement.Write).ShouldBe(AccessMode.Rw);
        AccessModePolicy.RequiredFor(AccessRequirement.Destructive).ShouldBe(AccessMode.Full);
    }

    [Theory]
    [InlineData("ro", AccessMode.Ro)]
    [InlineData("RW", AccessMode.Rw)]
    [InlineData("Full", AccessMode.Full)]
    [InlineData(" full ", AccessMode.Full)]
    public void Parse_AcceptsModesCaseInsensitively(string value, AccessMode expected) => AccessModePolicy.Parse(value).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("read-write")]
    public void Parse_RejectsUnknownValues(string? value) => AccessModePolicy.Parse(value).ShouldBeNull();

    [Fact]
    public void ProjectSettingKey_IsScopedToTheProjectId()
    {
        AccessModePolicy.ProjectSettingKey("acme-web").ShouldBe("access.mode.project:acme-web");
        AccessModePolicy.GlobalSettingKey.ShouldBe("access.mode.global");
    }
}
