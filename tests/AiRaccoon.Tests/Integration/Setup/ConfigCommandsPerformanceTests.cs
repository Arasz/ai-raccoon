using AiRaccoon.Core.Memory;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     Performance-config commands pin the settings-key contract the metrics writer (MetricsFlusher)
///     and reaper (MetricsRetentionJob) read: buffer capacity, flush interval, hot-table retention.
///     This is the CLI-only channel the checklist run (1.20.0, #352) found missing entirely — metrics
///     was the only settings-backed subsystem with zero CLI surface. Modelled directly on
///     ConfigCommandsMaintenanceTests.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsPerformanceTests
{
    private static Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store) =>
        CliRun.RunAsync(args, TestData.CreateConfigCommands(store, performance: new PerformanceCommands()));

    [Fact]
    public async Task BufferCapacitySet_WritesGlobalRow_AndStatesNextRestart()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["performance", "buffer-capacity", "5000"], store);

        exit.ShouldBe(0);
        store.Settings[MetricsConfigKeys.BufferCapacityGlobal].ShouldBe("5000");
        outp.ShouldContain("5000");
        outp.ShouldContain("next server restart");
    }

    [Fact]
    public async Task BufferCapacityInvalid_NonNumeric_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "buffer-capacity", "lots"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.BufferCapacityGlobal);
    }

    [Fact]
    public async Task BufferCapacityInvalid_Zero_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "buffer-capacity", "0"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.BufferCapacityGlobal);
    }

    [Fact]
    public async Task BufferCapacityInvalid_Negative_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "buffer-capacity", "-1"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.BufferCapacityGlobal);
    }

    /// <summary>An unbounded buffer capacity is an allocation the operator can make arbitrarily large.</summary>
    [Fact]
    public async Task BufferCapacityOutOfRange_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "buffer-capacity", "20000000000"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("measurements");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.BufferCapacityGlobal);
    }

    [Fact]
    public async Task FlushIntervalSet_WritesGlobalRow_AndStatesNextTick()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["performance", "flush-interval", "10"], store);

        exit.ShouldBe(0);
        store.Settings[MetricsConfigKeys.FlushIntervalSecondsGlobal].ShouldBe("10");
        outp.ShouldContain("10");
        outp.ShouldContain("next flush tick");
    }

    [Fact]
    public async Task FlushIntervalInvalid_NonNumeric_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "flush-interval", "often"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.FlushIntervalSecondsGlobal);
    }

    [Fact]
    public async Task FlushIntervalInvalid_Zero_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "flush-interval", "0"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.FlushIntervalSecondsGlobal);
    }

    [Fact]
    public async Task FlushIntervalInvalid_Negative_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "flush-interval", "-30"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.FlushIntervalSecondsGlobal);
    }

    [Fact]
    public async Task RetentionSet_WritesGlobalRow_AndStatesNextMaintenancePass()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["performance", "retention", "14"], store);

        exit.ShouldBe(0);
        store.Settings[MetricsConfigKeys.RetentionDaysGlobal].ShouldBe("14");
        outp.ShouldContain("14");
        outp.ShouldContain("next maintenance pass");
    }

    [Fact]
    public async Task RetentionInvalid_NonNumeric_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "retention", "forever"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.RetentionDaysGlobal);
    }

    [Fact]
    public async Task RetentionInvalid_Zero_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "retention", "0"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.RetentionDaysGlobal);
    }

    [Fact]
    public async Task RetentionInvalid_Negative_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "retention", "-7"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.RetentionDaysGlobal);
    }

    /// <summary>Retention days feeds DateTimeOffset.AddDays(-days) in MetricsRetentionJob — an
    /// unreasonably large value must be rejected at write time rather than crash the reaper.</summary>
    [Fact]
    public async Task RetentionOutOfRange_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["performance", "retention", "20000000000"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("days");
        store.Settings.ShouldNotContainKey(MetricsConfigKeys.RetentionDaysGlobal);
    }

    [Fact]
    public async Task List_ShowsDefaults_WhenUnset()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["performance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain(MetricsConfigKeys.DefaultBufferCapacity.ToString());
        outp.ShouldContain(MetricsConfigKeys.DefaultFlushIntervalSeconds.ToString());
        outp.ShouldContain(MetricsConfigKeys.DefaultRetentionDays.ToString());
    }

    /// <summary>A malformed row must fall back to the documented default (AC4), same contract as
    /// MetricsRetentionJobTests.AnInvalidRetentionSetting_FallsBackToTheDefault.</summary>
    [Fact]
    public async Task List_ShowsDefaults_WhenMalformed()
    {
        var store = new FakeConfigStore();
        store.Settings[MetricsConfigKeys.BufferCapacityGlobal] = "not-a-number";
        store.Settings[MetricsConfigKeys.FlushIntervalSecondsGlobal] = "not-a-number";
        store.Settings[MetricsConfigKeys.RetentionDaysGlobal] = "not-a-number";

        var (exit, outp, _) = await Run(["performance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain(MetricsConfigKeys.DefaultBufferCapacity.ToString());
        outp.ShouldContain(MetricsConfigKeys.DefaultFlushIntervalSeconds.ToString());
        outp.ShouldContain(MetricsConfigKeys.DefaultRetentionDays.ToString());
    }

    [Fact]
    public async Task List_ShowsConfiguredValues()
    {
        var store = new FakeConfigStore();
        store.Settings[MetricsConfigKeys.BufferCapacityGlobal] = "2500";
        store.Settings[MetricsConfigKeys.FlushIntervalSecondsGlobal] = "15";
        store.Settings[MetricsConfigKeys.RetentionDaysGlobal] = "60";

        var (exit, outp, _) = await Run(["performance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("2500");
        outp.ShouldContain("15");
        outp.ShouldContain("60");
    }

    /// <summary>AC5 — list output also states the per-knob take-effect timing, not just the values.</summary>
    [Fact]
    public async Task List_StatesTakeEffectTiming_PerKnob()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["performance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("next server restart");
        outp.ShouldContain("next flush tick");
        outp.ShouldContain("next maintenance pass");
    }

    [Fact]
    public async Task ListAlias_Show_ResolvesToList()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["performance", "show"], store);

        exit.ShouldBe(0);
        outp.ShouldContain(MetricsConfigKeys.DefaultBufferCapacity.ToString());
    }
}
