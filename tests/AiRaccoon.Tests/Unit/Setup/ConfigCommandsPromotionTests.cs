using AiRaccoon.Core.Memory.Promotion;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Promotion-model commands pin the settings-key contract the promotion classifier reads:
///     promotion.model.enabled (default disabled).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsPromotionTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        parsed!.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await TestData.CreateConfigCommands(store, promotion: new PromotionCommands())
            .RunAsync(parsed!, new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task PromotionModelEnable_WritesEnabledRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["promotion", "model", "enable"], store);

        exit.ShouldBe(0);
        store.Settings[PromotionModelSettings.EnabledKey].ShouldBe("true");
        outp.ShouldContain("promotion model enabled");
    }

    [Fact]
    public async Task PromotionModelDisable_WritesDisabledRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["promotion", "model", "disable"], store);

        exit.ShouldBe(0);
        store.Settings[PromotionModelSettings.EnabledKey].ShouldBe("false");
        outp.ShouldContain("promotion model disabled");
    }

    [Fact]
    public async Task PromotionModelShow_DefaultsToDisabled_WhenUnset()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["promotion", "model", "show"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("promotion model: disabled");
    }

    [Fact]
    public async Task PromotionModelShow_ShowsEnabled_WhenSet()
    {
        var store = new FakeConfigStore();
        store.Settings[PromotionModelSettings.EnabledKey] = "true";

        var (exit, outp, _) = await Run(["promotion", "model", "show"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("promotion model: enabled");
    }
}
