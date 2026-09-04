using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     Metrics settings keys pin the settings-table contract the writer and reaper read: buffer
///     capacity (default 1000), flush interval (default 30s), hot-table retention (default 28
///     days); bad values fall back to the defaults, never throw. Shape copied from
///     <see cref="BankMaintenanceConfigKeys" /> (BankMaintenanceConfigKeysTests is the twin).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MetricsConfigKeysTests
{
    [Fact]
    public void KeyNames_AreTheSettingsContract()
    {
        MetricsConfigKeys.BufferCapacityGlobal.ShouldBe("metrics.buffer-capacity.global");
        MetricsConfigKeys.FlushIntervalSecondsGlobal.ShouldBe("metrics.flush-interval-seconds.global");
        MetricsConfigKeys.RetentionDaysGlobal.ShouldBe("metrics.retention-days.global");
    }

    [Fact]
    public void Defaults_Are1000Buffer_30SecondFlush_28DayRetention()
    {
        MetricsConfigKeys.DefaultBufferCapacity.ShouldBe(1000);
        MetricsConfigKeys.DefaultFlushIntervalSeconds.ShouldBe(30);
        MetricsConfigKeys.DefaultRetentionDays.ShouldBe(28);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void ParseBufferCapacity_Invalid_ReturnsDefault(string? value)
    {
        MetricsConfigKeys.ParseBufferCapacity(value).ShouldBe(MetricsConfigKeys.DefaultBufferCapacity);
    }

    [Fact]
    public void ParseBufferCapacity_Positive_Parses()
    {
        MetricsConfigKeys.ParseBufferCapacity("500").ShouldBe(500);
        MetricsConfigKeys.ParseBufferCapacity("5000").ShouldBe(5000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ParseFlushIntervalSeconds_Invalid_ReturnsDefault(string? value)
    {
        MetricsConfigKeys.ParseFlushIntervalSeconds(value).ShouldBe(MetricsConfigKeys.DefaultFlushIntervalSeconds);
    }

    [Fact]
    public void ParseFlushIntervalSeconds_Positive_Parses()
    {
        MetricsConfigKeys.ParseFlushIntervalSeconds("5").ShouldBe(5);
        MetricsConfigKeys.ParseFlushIntervalSeconds("60").ShouldBe(60);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ParseRetentionDays_Invalid_ReturnsDefault(string? value)
    {
        MetricsConfigKeys.ParseRetentionDays(value).ShouldBe(MetricsConfigKeys.DefaultRetentionDays);
    }

    [Fact]
    public void ParseRetentionDays_Positive_Parses()
    {
        MetricsConfigKeys.ParseRetentionDays("7").ShouldBe(7);
        MetricsConfigKeys.ParseRetentionDays("90").ShouldBe(90);
    }

    /// <summary>
    ///     WP11 (log-values-as-metrics) + #601 fusion signals: one constant set both MetricsReportService's discovery and
    ///     every internal recorder's naming consume, so a sixth family cannot be added on one side
    ///     only (derive-or-delete).
    /// </summary>
    [Fact]
    public void InternalSeriesPrefixes_CoversJobDrainWriteAndSearchQueryAndSearchFusion()
    {
        MetricsConfigKeys.InternalSeriesPrefixes.ShouldBe(["job.", "drain.", "write.", "search.query.", "search.fusion."]);
    }

    [Fact]
    public void FusionMetricNames_AreSearchFusionPrefixed()
    {
        FusionStats.MetricNames.ShouldNotBeEmpty();
        foreach (var name in FusionStats.MetricNames)
        {
            name.StartsWith(MetricsConfigKeys.SearchFusionMetricPrefix, StringComparison.Ordinal).ShouldBeTrue($"{name} must live under {MetricsConfigKeys.SearchFusionMetricPrefix} so prefix-discovery surfaces it");
        }
    }

    [Fact]
    public void DrainMetricNames_AreDrainPrefixed()
    {
        MetricsConfigKeys.DrainRowsMetricName("code").ShouldBe("drain.code.rows");
        MetricsConfigKeys.DrainDurationMetricName("memory").ShouldBe("drain.memory.duration_ms");
    }

    [Fact]
    public void WriteAndSearchQueryMetricNames_AreTheFixedContract()
    {
        MetricsConfigKeys.ReplaceWaitMsMetricName.ShouldBe("write.replace.wait_ms");
        MetricsConfigKeys.ReplaceHeldMsMetricName.ShouldBe("write.replace.held_ms");
        MetricsConfigKeys.ReplaceRowsMetricName.ShouldBe("write.replace.rows");
        MetricsConfigKeys.QueryTruncatedTokensMetricName.ShouldBe("search.query.truncated_tokens");
    }
}
