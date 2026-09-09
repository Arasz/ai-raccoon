using AiRaccoon.Hosting.Common;
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
        // Dead on parse: nothing launched, so no bank and no token file exist.
        File.Exists(Path.Combine(_dataRoot, "memory.db")).ShouldBeFalse();
        File.Exists(Path.Combine(_dataRoot, McpTokenFile.FileName)).ShouldBeFalse();
    }

    [Theory]
    // ADR-0104 D1: every spelling that names stdio dies at parse with the hint plus a
    // rejection naming the surviving pair — including the System.CommandLine colon form
    // (--transport:stdio) and an all-caps value.
    [InlineData("--transport", "stdio")]
    [InlineData("--transport=stdio")]
    [InlineData("--transport:stdio")]
    [InlineData("--transport", "STDIO")]
    public void Parse_RemovedStdioSpellings_AreRejectedWithHintAndRejection(params string[] transportFlag)
    {
        CliArgs.TryParse([.. transportFlag, "--data-root", _dataRoot], out var parsed);

        parsed!.Errors.ShouldContain(e => e.Contains("--transport stdio was removed", StringComparison.Ordinal));
        parsed.Errors.ShouldContain(e => e.Contains("proxy|http", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RemovedTransport_LastOccurrenceWins()
    {
        CliArgs.TryParse(["--transport", "proxy", "--transport", "stdio", "--data-root", _dataRoot], out var last);

        last!.Errors.ShouldContain(e => e.Contains("--transport stdio was removed", StringComparison.Ordinal));

        CliArgs.TryParse(["--transport", "stdio", "--transport", "proxy", "--data-root", _dataRoot], out var first);

        first!.Errors.ShouldNotBeEmpty();
        first.Errors.ShouldNotContain(e => e.Contains("--transport stdio was removed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--transport", "stdio")]
    [InlineData("--transport=stdio")]
    [InlineData("--transport:stdio")]
    public void Parse_ServeWithRemovedStdio_FailsParse(params string[] transportFlag)
    {
        CliArgs.TryParse([.. transportFlag, "--data-root", _dataRoot, "serve"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
        parsed.Errors.ShouldContain(e => e.Contains("--transport stdio was removed", StringComparison.Ordinal));
        parsed.CommandPath.ShouldBe(["serve"]);
    }

    [Fact]
    public void Parse_ServeWithRemovedStdio_PostVerb_FailsParse()
    {
        CliArgs.TryParse(["serve", "--transport", "stdio", "--data-root", _dataRoot], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
        parsed.Errors.ShouldContain(e => e.Contains("--transport stdio was removed", StringComparison.Ordinal));
        parsed.CommandPath.ShouldBe(["serve"]);
    }

    [Theory]
    [InlineData("--transport", "stdio")]
    [InlineData("--transport=stdio")]
    [InlineData("--transport:stdio")]
    public async Task Serve_RemovedStdioSpellings_ReturnFifteen(params string[] transportFlag)
    {
        var runner = new AppRunner();

        var exit = await runner.Run([.. transportFlag, "--data-root", _dataRoot, "serve"]);

        exit.ShouldBe(ExitCode.InvalidArgument);
    }

    [Fact]
    public async Task Serve_RemovedStdio_PostVerb_ReturnsFifteen()
    {
        var runner = new AppRunner();

        var exit = await runner.Run(["serve", "--transport", "stdio", "--data-root", _dataRoot]);

        exit.ShouldBe(ExitCode.InvalidArgument);
    }

    [Fact]
    public async Task Bare_PortZero_ReturnsSixWithoutDialling()
    {
        var runner = new AppRunner();

        var exit = await runner.Run(["--data-root", _dataRoot, "--port", "0"]);

        exit.ShouldBe(ExitCode.ProxyBackendUnavailable);
    }

    [Theory]
    [InlineData("--transport", "proxy")]
    [InlineData("--transport=proxy")]
    public void Parse_ServeWithProxy_PreVerb_ParsesCleanly(params string[] transportFlag)
    {
        var ok = CliArgs.TryParse([.. transportFlag, "--data-root", _dataRoot, "serve"], out var parsed);

        ok.ShouldBeTrue();
        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
    }

    [Fact]
    public void Parse_ServeWithProxy_PostVerb_IsRejected()
    {
        // serve takes no --transport at all (ADR-0104): a root option after the verb is
        // unrecognized, so the post-verb shape fails parse — pinned here, not clean.
        var ok = CliArgs.TryParse(["serve", "--transport", "proxy", "--data-root", _dataRoot], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
    }

    private static string RenderErrors(CliInput parsed)
    {
        var writer = new StringWriter();
        parsed.RenderTo(new StandardStreams(TextReader.Null, TextWriter.Null, writer));
        return writer.ToString();
    }
}
