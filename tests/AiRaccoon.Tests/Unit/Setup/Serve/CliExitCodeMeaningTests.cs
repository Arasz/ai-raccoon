using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     One meaning per code, on every path (ADR-0106 D4): argv that does not fit the command
///     grammar — an unknown or misplaced token, a missing subcommand — is unparseable (9); argv
///     that fits but carries a missing or invalid value is InvalidArgument (15). None of these
///     rows may launch or dispatch anything.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliExitCodeMeaningTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-exit-meaning");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Theory]
    [InlineData("bogusverb")]
    [InlineData("--attach")]
    [InlineData("serve", "--attach")]
    [InlineData("settings", "noise", "show", "extra")]
    [InlineData("settings", "sweep")]
    [InlineData("settings", "sweep", "bogus")]
    [InlineData("serve", "--transport", "stdio")]
    public async Task ArgvOutsideTheGrammar_IsUnparseable(params string[] args)
    {
        var exitCode = await new AppRunner().Run(["--data-root", _dataRoot, .. args]);

        exitCode.ShouldBe(ExitCode.FailedToParseCliArgs);
        File.Exists(Path.Combine(_dataRoot, "memory.db")).ShouldBeFalse("nothing may launch on a bad argv");
    }

    [Theory]
    [InlineData("settings", "access", "set")]
    [InlineData("settings", "sweep", "threshold", "set")]
    [InlineData("serve", "--idle-timeout", "4x")]
    [InlineData("serve", "--mcp-entry", "--format", "bogus")]
    [InlineData("--transport", "stdio", "serve")]
    [InlineData("--transport", "stdio")]
    [InlineData("--port", "abc")]
    [InlineData("--install-scope", "bogus")]
    [InlineData("--transport", "https")]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "0", "settings", "sweep", "show")]
    [InlineData("--port", "70000", "settings", "sweep", "show")]
    [InlineData("serve", "--port", "70000")]
    [InlineData("serve", "observability", "pid", "--port", "0")]
    public async Task ArgvWithAnInvalidOrMissingValue_IsInvalidArgument(params string[] args)
    {
        var exitCode = await new AppRunner().Run(["--data-root", _dataRoot, .. args]);

        exitCode.ShouldBe(ExitCode.InvalidArgument);
        File.Exists(Path.Combine(_dataRoot, "memory.db")).ShouldBeFalse("nothing may launch on a bad argv");
    }
}
