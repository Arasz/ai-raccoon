using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Render;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     P1 transport removal: the CLI surface is proxy|http only. stdio (+https) is rejected
///     with a hint naming the replacement, still exit 9 for bare launches.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TransportRemovalTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-transport-removal");

    public void Dispose()
    {
        TestData.DeleteTempRoot(_dataRoot);
    }

    [Fact]
    public void Render_Help_ListsOnlyProxyAndHttp()
    {
        var writer = new StringWriter();
        CliArgs.TryParse(["--help"], out var parsed).ShouldBeTrue();

        parsed!.RenderTo(new StandardStreams(TextReader.Null, TextWriter.Null, writer));

        var help = writer.ToString();
        help.ShouldContain("proxy");
        help.ShouldContain("http");
        help.ShouldNotContain("stdio");
        help.ShouldNotContain("https");
    }

    [Fact]
    public void Parse_Stdio_IsRejectedWithHintNamingTheReplacement()
    {
        CliArgs.TryParse(["--transport", "stdio", "--data-root", _dataRoot], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
        var rendered = RenderErrors(parsed);
        rendered.ShouldContain("--transport");
        rendered.ShouldContain("stdio");
        rendered.ShouldContain("proxy");
        rendered.ShouldContain("serve");
    }

    [Fact]
    public void Parse_Https_IsRejected()
    {
        CliArgs.TryParse(["--transport", "https", "--data-root", _dataRoot], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("http")]
    public void Parse_SurvivingTransports_StillParseCleanly(string transport)
    {
        var ok = CliArgs.TryParse(["--transport", transport, "--data-root", _dataRoot], out var parsed);

        ok.ShouldBeTrue();
        parsed!.Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("http")]
    public void Serve_SurvivingTransports_ParseCleanly(string transport)
    {
        var ok = CliArgs.TryParse(["--transport", transport, "--data-root", _dataRoot, "serve"], out var parsed);

        ok.ShouldBeTrue();
        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("https")]
    public void Serve_RemovedTransports_AreRejected(string transport)
    {
        CliArgs.TryParse(["--transport", transport, "--data-root", _dataRoot, "serve"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AppRunner_Stdio_ReturnsNineWithoutLaunching()
    {
        var runner = new AppRunner();

        var exitCode = await runner.Run(["--transport", "stdio", "--data-root", _dataRoot]);

        exitCode.ShouldBe(ExitCode.FailedToParseCliArgs);
    }

    private static string RenderErrors(CliInput parsed)
    {
        var writer = new StringWriter();
        parsed.RenderTo(new StandardStreams(TextReader.Null, TextWriter.Null, writer));
        return writer.ToString();
    }
}
