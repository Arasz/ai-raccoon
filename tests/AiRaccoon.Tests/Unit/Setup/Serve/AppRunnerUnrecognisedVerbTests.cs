using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     An unrecognised verb must fail, not launch the proxy (docs/adr/0060). `CliArgs.TryParse`
///     reports success whenever the *option* read succeeds, so parse errors were rendered to stderr
///     and then ignored — and the run continued into the proxy, which dials the DEFAULT port and
///     the DEFAULT token file, ignoring the `--data-root` the caller passed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class AppRunnerUnrecognisedVerbTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-unrecognised-verb");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The 1.13.0 and 1.14.0 release checklists both recorded this as a failure. On 1.14.0 the
    ///     probe exited 6 (ProxyBackendUnavailable) — the proxy's honest verdict, reached only
    ///     because it should never have been asked. Exit 9 is the parse failure it is.
    /// </summary>
    [Fact]
    public async Task UnrecognisedVerb_FailsToParse_AndNeverReachesTheProxy()
    {
        var runner = new AppRunner();

        var exitCode = await runner.Run(["--data-root", _dataRoot, "notaverb"]);

        exitCode.ShouldBe(ErrorCode.Usage.Unparseable);
    }

    /// <summary>An unknown option is the same mistake in the other shape.</summary>
    [Fact]
    public async Task UnknownOption_FailsToParse()
    {
        var runner = new AppRunner();

        var exitCode = await runner.Run(["--data-root", _dataRoot, "--not-an-option", "x"]);

        exitCode.ShouldBe(ErrorCode.Usage.Unparseable);
    }

    /// <summary>
    ///     ADR-0106 D4: an unrecognized token is unparseable whether it comes before or after a
    ///     verb, so a stray `--attach` exits 9 in both spellings — not the verb's 15.
    /// </summary>
    [Theory]
    [InlineData("--attach")]
    [InlineData("serve", "--attach")]
    [InlineData("settings", "noise", "show", "--attach")]
    public async Task StrayAttach_FailsToParse_InEitherSpelling(params string[] args)
    {
        var runner = new AppRunner();

        var exitCode = await runner.Run(["--data-root", _dataRoot, .. args]);

        exitCode.ShouldBe(ErrorCode.Usage.Unparseable);
    }

    /// <summary>
    ///     The other half of D4: a known verb whose own argument fails validation keeps
    ///     InvalidArgument (15), so the 9 above cannot be won by mapping every verb error to 9.
    /// </summary>
    [Fact]
    public async Task KnownVerb_MissingRequiredArgument_IsMissingValue()
    {
        var runner = new AppRunner();

        var exitCode = await runner.Run(["--data-root", _dataRoot, "settings", "access", "set"]);

        exitCode.ShouldBe(ErrorCode.Usage.MissingValue);
    }

    /// <summary>
    ///     The guard on the guard: a bare invocation carries no parse error, so it must still be
    ///     free to launch. Without this, "make every parse error fatal" could be satisfied by
    ///     refusing everything.
    /// </summary>
    [Fact]
    public void BareInvocation_CarriesNoParseError()
    {
        CliArgs.TryParse([], out var parsed).ShouldBeTrue();

        parsed!.Errors.ShouldBeEmpty();
        parsed.ServerConfig.Transport.ShouldBe(McpTransport.Proxy, "a bare invocation is the proxy entry point (ADR-0020)");
    }

    /// <summary>And a real verb with real arguments must still parse clean.</summary>
    [Fact]
    public void KnownVerb_CarriesNoParseError()
    {
        CliArgs.TryParse(["--data-root", _dataRoot, "settings", "noise", "show"], out var parsed).ShouldBeTrue();

        parsed!.Errors.ShouldBeEmpty();
        parsed.IsCommandInput.ShouldBeTrue();
    }
}
