using AiRaccoon.Core.Memory;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Extract-config commands pin the settings-key contract the background extraction
///     service reads: extract.enabled.global ("true"/"false", default off),
///     extract.mode.global (propose|promote, default propose),
///     extract.interval-minutes.global (default 30).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsExtractTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr,
            TextReader.Null, extract: new ExtractCommands(),
            cancellationToken: TestContext.Current.CancellationToken);
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
}
