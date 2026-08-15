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

        exitCode.ShouldBe(ExitCode.FailedToParseCliArgs);
    }

    /// <summary>An unknown option is the same mistake in the other shape.</summary>
    [Fact]
    public async Task UnknownOption_FailsToParse()
    {
        var runner = new AppRunner();

        var exitCode = await runner.Run(["--data-root", _dataRoot, "--not-an-option", "x"]);

        exitCode.ShouldBe(ExitCode.FailedToParseCliArgs);
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
        parsed.IsProxyInput.ShouldBeTrue("a bare invocation is the proxy entry point (ADR-0020)");
    }

    /// <summary>And a real verb with real arguments must still parse clean.</summary>
    [Fact]
    public void KnownVerb_CarriesNoParseError()
    {
        CliArgs.TryParse(["--data-root", _dataRoot, "noise", "show"], out var parsed).ShouldBeTrue();

        parsed!.Errors.ShouldBeEmpty();
        parsed.IsCommandInput.ShouldBeTrue();
    }
}
