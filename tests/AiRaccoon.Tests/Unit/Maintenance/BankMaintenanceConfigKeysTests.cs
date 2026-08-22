using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     Bank-maintenance settings keys pin the settings-table contract the hosted service reads:
///     checkpoint interval (default 60 min) and vacuum interval (default 7 days); bad values
///     fall back to the defaults, never throw.
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

    /// <summary>WP11-C (G18): today's 4 * EntryEmbedder.BatchSize, unchanged behaviour on day one.</summary>
    [Fact]
    public void EmbedRowsPerRun_KeyAndDefault_AreTheContract()
    {
        BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal.ShouldBe("maintenance.embed-rows-per-run.global");
        BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun.ShouldBe(128);
    }

    [Fact]
    public void TryParseEmbedRowsPerRun_Unset_IsValid_AndDefaults()
    {
        BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun(null, out var rows).ShouldBeTrue();
        rows.ShouldBe(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);

        BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun("", out var rowsEmpty).ShouldBeTrue();
        rowsEmpty.ShouldBe(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);
    }

    [Fact]
    public void TryParseEmbedRowsPerRun_Positive_Parses()
    {
        BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun("7", out var rows).ShouldBeTrue();
        rows.ShouldBe(7);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void TryParseEmbedRowsPerRun_Garbage_IsInvalid_ButStillReturnsTheDefault(string value)
    {
        BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun(value, out var rows).ShouldBeFalse();
        rows.ShouldBe(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);
    }

    [Fact]
    public void ParseEmbedRowsPerRun_MirrorsTryParse_WithoutTheValidityFlag()
    {
        BankMaintenanceConfigKeys.ParseEmbedRowsPerRun("7").ShouldBe(7);
        BankMaintenanceConfigKeys.ParseEmbedRowsPerRun("garbage").ShouldBe(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);
        BankMaintenanceConfigKeys.ParseEmbedRowsPerRun(null).ShouldBe(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);
    }

    /// <summary>Review finding 1 (#517): 4096 = 128 * EntryEmbedder/CodeEmbedder.BatchSize (32) — generous headroom above the 128 default while still bounding the drain's one-shot `SELECT ... LIMIT` materialisation to a modest burst.</summary>
    [Fact]
    public void MaxEmbedRowsPerRun_IsTheDocumentedCeiling()
    {
        BankMaintenanceConfigKeys.MaxEmbedRowsPerRun.ShouldBe(4096);
    }

    /// <summary>Review finding 1: clamp at the parse layer (mirrors ParseVacuumIntervalDays_ClampsAbsurdValues_ToTheTimeSpanSafeCeiling above) — and, unlike vacuum, this is also the CLI's one shared validity rule (finding 4), so an over-ceiling value is invalid, not silently accepted.</summary>
    [Fact]
    public void TryParseEmbedRowsPerRun_OverCeiling_ClampsToTheCeiling_AndIsInvalid()
    {
        BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun("2000000000", out var rows).ShouldBeFalse();
        rows.ShouldBe(BankMaintenanceConfigKeys.MaxEmbedRowsPerRun);

        BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun(
            (BankMaintenanceConfigKeys.MaxEmbedRowsPerRun + 1).ToString(), out var rowsJustOver).ShouldBeFalse();
        rowsJustOver.ShouldBe(BankMaintenanceConfigKeys.MaxEmbedRowsPerRun);
    }

    [Fact]
    public void TryParseEmbedRowsPerRun_AtTheCeiling_IsValid()
    {
        BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun(
            BankMaintenanceConfigKeys.MaxEmbedRowsPerRun.ToString(), out var rows).ShouldBeTrue();
        rows.ShouldBe(BankMaintenanceConfigKeys.MaxEmbedRowsPerRun);
    }

    [Fact]
    public void ParseEmbedRowsPerRun_OverCeiling_ClampsToTheCeiling()
    {
        BankMaintenanceConfigKeys.ParseEmbedRowsPerRun("2000000000")
            .ShouldBe(BankMaintenanceConfigKeys.MaxEmbedRowsPerRun);
    }
}
