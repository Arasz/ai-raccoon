using AiRaccoon.Core.Memory;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Extract-config commands pin the settings-key contract the background extraction service
///     reads: extract.enabled.global, extract.mode.global, extract.interval-minutes.global.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsExtractTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        parsed!.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await TestData.CreateConfigCommands(store, extract: new ExtractCommands(TestData.UnusedPromotionQueueStore()))
            .RunAsync(parsed!, new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task ExtractEnableTrue_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["extract", "enable", "true"], store);

        exit.ShouldBe(0);
        store.Settings[ExtractionConfigKeys.EnabledGlobal].ShouldBe("true");
        outp.ShouldContain("shared extraction enabled");
    }

    [Fact]
    public async Task ExtractEnableFalse_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        await Run(["extract", "enable", "false"], store);

        store.Settings[ExtractionConfigKeys.EnabledGlobal].ShouldBe("false");
    }

    [Fact]
    public async Task ExtractModePromote_WritesRow_AndWarnsAboutCrossProjectSharing()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["extract", "mode", "promote"], store);

        exit.ShouldBe(0);
        store.Settings[ExtractionConfigKeys.ModeGlobal].ShouldBe("promote");
        outp.ShouldContain("shares candidates with ALL projects");
    }

    [Fact]
    public async Task ExtractModePropose_WritesRow()
    {
        var store = new FakeConfigStore();

        await Run(["extract", "mode", "propose"], store);

        store.Settings[ExtractionConfigKeys.ModeGlobal].ShouldBe("propose");
    }

    [Fact]
    public async Task ExtractModeInvalid_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["extract", "mode", "auto"], store);

        exit.ShouldBe(1);
        err.ShouldContain("mode must be 'propose' or 'promote'");
        store.Settings.ShouldNotContainKey(ExtractionConfigKeys.ModeGlobal);
    }

    [Fact]
    public async Task ExtractIntervalSet_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["extract", "interval", "30"], store);

        exit.ShouldBe(0);
        store.Settings[ExtractionConfigKeys.IntervalMinutesGlobal].ShouldBe("30");
        outp.ShouldContain("interval: 30 min");
    }

    [Fact]
    public async Task ExtractIntervalInvalid_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["extract", "interval", "0"], store);

        exit.ShouldBe(1);
        err.ShouldContain("interval must be a positive number of minutes");
        store.Settings.ShouldNotContainKey(ExtractionConfigKeys.IntervalMinutesGlobal);
    }

    [Fact]
    public async Task ExtractIntervalNonNumeric_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["extract", "interval", "often"], store);

        exit.ShouldBe(1);
        err.ShouldContain("interval must be a positive number of minutes");
        store.Settings.ShouldNotContainKey(ExtractionConfigKeys.IntervalMinutesGlobal);
    }

    [Fact]
    public async Task ExtractCapacitySet_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["extract", "capacity", "3"], store);

        exit.ShouldBe(0);
        store.Settings[ExtractionConfigKeys.QueueCapacityGlobal].ShouldBe("3");
        outp.ShouldContain("capacity: 3 candidates");
    }

    [Fact]
    public async Task ExtractCapacityInvalid_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["extract", "capacity", "0"], store);

        exit.ShouldBe(1);
        err.ShouldContain("capacity must be a positive number");
        store.Settings.ShouldNotContainKey(ExtractionConfigKeys.QueueCapacityGlobal);
    }

    [Fact]
    public async Task ExtractList_ShowsTheQueueCapacity()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["extract", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("queue-capacity: 1000");
    }

    [Fact]
    public async Task ExtractList_ShowsDefaults_WhenUnset()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["extract", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("enabled: False");
        outp.ShouldContain("mode: propose");
        outp.ShouldContain("interval: 30 min");
    }

    [Fact]
    public async Task ExtractList_ShowsConfiguredValues()
    {
        var store = new FakeConfigStore();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Settings[ExtractionConfigKeys.IntervalMinutesGlobal] = "30";

        var (_, outp, _) = await Run(["extract", "list"], store);

        outp.ShouldContain("enabled: True");
        outp.ShouldContain("mode: promote");
        outp.ShouldContain("interval: 30 min");
    }

    [Fact]
    public async Task ExtractExcludeAdd_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["extract", "exclude", "add", "hermes/"], store);

        exit.ShouldBe(0);
        store.Settings[ExtractionConfigKeys.ExcludePrefixesGlobal].ShouldBe("hermes/");
        outp.ShouldContain("hermes/");
    }

    [Fact]
    public async Task ExtractExcludeAdd_DedupesExistingPrefix()
    {
        var store = new FakeConfigStore();
        store.Settings[ExtractionConfigKeys.ExcludePrefixesGlobal] = "hermes/,docs/";

        var (exit, _, _) = await Run(["extract", "exclude", "add", "hermes/"], store);

        exit.ShouldBe(0);
        store.Settings[ExtractionConfigKeys.ExcludePrefixesGlobal].ShouldBe("hermes/,docs/");
    }

    [Fact]
    public async Task ExtractExcludeRemove_DeletesPrefix()
    {
        var store = new FakeConfigStore();
        store.Settings[ExtractionConfigKeys.ExcludePrefixesGlobal] = "hermes/,docs/";

        var (exit, outp, _) = await Run(["extract", "exclude", "remove", "hermes/"], store);

        exit.ShouldBe(0);
        store.Settings[ExtractionConfigKeys.ExcludePrefixesGlobal].ShouldBe("docs/");
        outp.ShouldContain("docs/");
    }

    [Fact]
    public async Task ExtractExcludeRemove_LastPrefix_DeletesTheSettingRow()
    {
        var store = new FakeConfigStore();
        store.Settings[ExtractionConfigKeys.ExcludePrefixesGlobal] = "hermes/";

        var (exit, _, _) = await Run(["extract", "exclude", "remove", "hermes/"], store);

        exit.ShouldBe(0);
        store.Settings.ShouldNotContainKey(ExtractionConfigKeys.ExcludePrefixesGlobal);
    }

    [Fact]
    public async Task ExtractExcludeList_ShowsNone_WhenUnset()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["extract", "exclude", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("none");
    }

    [Fact]
    public async Task ExtractExcludeList_ShowsConfiguredPrefixes()
    {
        var store = new FakeConfigStore();
        store.Settings[ExtractionConfigKeys.ExcludePrefixesGlobal] = "hermes/,docs/";

        var (exit, outp, _) = await Run(["extract", "exclude", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("hermes/");
        outp.ShouldContain("docs/");
    }
}
