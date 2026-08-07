using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     Bank-maintenance settings keys pin the settings-table contract the hosted
///     service reads: maintenance.checkpoint-interval-minutes.global (default 60)
///     and maintenance.vacuum-interval-days.global (default 7); bad values fall back
///     to the defaults, never throw.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BankMaintenanceConfigKeysTests
{
    [Fact]
    public void KeyNames_AreTheSettingsContract()
    {
        BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal
            .ShouldBe("maintenance.checkpoint-interval-minutes.global");
        BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal
            .ShouldBe("maintenance.vacuum-interval-days.global");
    }

    [Fact]
    public void Defaults_Are60Minutes_And7Days()
    {
        BankMaintenanceConfigKeys.DefaultCheckpointIntervalMinutes.ShouldBe(60);
        BankMaintenanceConfigKeys.DefaultVacuumIntervalDays.ShouldBe(7);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void ParseCheckpointIntervalMinutes_Invalid_ReturnsDefault(string? value)
    {
        BankMaintenanceConfigKeys.ParseCheckpointIntervalMinutes(value)
            .ShouldBe(BankMaintenanceConfigKeys.DefaultCheckpointIntervalMinutes);
    }

    [Fact]
    public void ParseCheckpointIntervalMinutes_Positive_Parses()
    {
        BankMaintenanceConfigKeys.ParseCheckpointIntervalMinutes("15").ShouldBe(15);
        BankMaintenanceConfigKeys.ParseCheckpointIntervalMinutes("1440").ShouldBe(1440);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ParseVacuumIntervalDays_Invalid_ReturnsDefault(string? value)
    {
        BankMaintenanceConfigKeys.ParseVacuumIntervalDays(value)
            .ShouldBe(BankMaintenanceConfigKeys.DefaultVacuumIntervalDays);
    }

    [Fact]
    public void ParseVacuumIntervalDays_Positive_Parses()
    {
        BankMaintenanceConfigKeys.ParseVacuumIntervalDays("1").ShouldBe(1);
        BankMaintenanceConfigKeys.ParseVacuumIntervalDays("30").ShouldBe(30);
    }

    [Fact]
    public void ParseVacuumIntervalDays_ClampsAbsurdValues_ToTheTimeSpanSafeCeiling()
    {
        // TimeSpan.FromDays overflows past ~10.7M days; the parse must clamp so the
        // service can never throw OverflowException on a settings value.
        BankMaintenanceConfigKeys.ParseVacuumIntervalDays("20000000")
            .ShouldBe(BankMaintenanceConfigKeys.MaxVacuumIntervalDays);
        BankMaintenanceConfigKeys.ParseVacuumIntervalDays(
            BankMaintenanceConfigKeys.MaxVacuumIntervalDays.ToString()).ShouldBe(
            BankMaintenanceConfigKeys.MaxVacuumIntervalDays);
    }
}
