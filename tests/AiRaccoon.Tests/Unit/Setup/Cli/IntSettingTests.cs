using System.CommandLine;
using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Setup.Cli.Commands;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>A CLI integer is the same number on every machine: it parses and stores with the invariant culture, like the sibling setters.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IntSettingTests
{
    private static readonly IntSetting Setting = new("some.key", "minutes", "interval", "minutes", n => $"set {n}");

    [Fact]
    public async Task SetAsync_PlainNumber_StoresItAndConfirms()
    {
        var store = Substitute.For<IMemoryStore>();
        var (streams, output, _) = Streams();

        var code = await Setting.SetAsync(Parse("15"), store, streams, TestContext.Current.CancellationToken);

        code.ShouldBe(0);
        await store.Received(1).SetSettingAsync("some.key", "15", TestContext.Current.CancellationToken);
        output.ToString().ShouldContain("set 15");
    }

    [Fact]
    public async Task SetAsync_UnderACultureWithACustomPositiveSign_RejectsWhatInvariantRejects()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PositiveSign = "plus";
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            var store = Substitute.For<IMemoryStore>();
            var (streams, _, error) = Streams();

            var code = await Setting.SetAsync(Parse("plus5"), store, streams, TestContext.Current.CancellationToken);

            code.ShouldBe(ErrorCode.Usage.InvalidValue);
            error.ToString().ShouldContain("must be a positive number");
            await store.DidNotReceiveWithAnyArgs().SetSettingAsync(default!, default!, TestContext.Current.CancellationToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ParseResult Parse(string value) =>
        new Command("set") { new Argument<string>("minutes") }.Parse([value]);

    private static (StandardStreams Streams, StringWriter Output, StringWriter Error) Streams()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        return (new StandardStreams(TextReader.Null, output, error), output, error);
    }
}
