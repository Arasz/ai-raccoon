using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     One meaning per code, on every path (ADR-0107): argv outside the command grammar is
///     Unparseable; argv that fits it exits with the Usage case its bad value names. None of these
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

        exitCode.ShouldBe(ErrorCode.Usage.Unparseable);
        File.Exists(Path.Combine(_dataRoot, "memory.db")).ShouldBeFalse("nothing may launch on a bad argv");
    }

    [Theory]
    [InlineData("serve", "--idle-timeout", "4x")]
    [InlineData("serve", "--mcp-entry", "--format", "bogus")]
    [InlineData("--port", "abc")]
    [InlineData("--install-scope", "bogus")]
    [InlineData("serve", "--port", "70000")]
    public async Task ArgvWithAnInvalidValue_IsInvalidValue(params string[] args)
    {
        await ShouldExitWithoutLaunching(args, ErrorCode.Usage.InvalidValue);
    }

    [Theory]
    [InlineData("settings", "access", "set")]
    [InlineData("settings", "sweep", "threshold", "set")]
    public async Task ArgvMissingARequiredValue_IsMissingValue(params string[] args)
    {
        await ShouldExitWithoutLaunching(args, ErrorCode.Usage.MissingValue);
    }

    [Theory]
    [InlineData("--transport", "stdio", "serve")]
    [InlineData("--transport", "stdio")]
    [InlineData("--transport", "https")]
    public async Task ARemovedTransport_IsRemovedTransport(params string[] args)
    {
        await ShouldExitWithoutLaunching(args, ErrorCode.Usage.RemovedTransport);
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "0", "settings", "sweep", "show")]
    [InlineData("--port", "70000", "settings", "sweep", "show")]
    [InlineData("serve", "observability", "pid", "--port", "0")]
    [InlineData("serve", "observability", "pid", "--port", "70000")]
    public async Task APortThatMustBeDialledButCannotBe_IsUndialablePort(params string[] args)
    {
        await ShouldExitWithoutLaunching(args, ErrorCode.Usage.UndialablePort);
    }

    private async Task ShouldExitWithoutLaunching(string[] args, int expected)
    {
        var exitCode = await new AppRunner().Run(["--data-root", _dataRoot, .. args]);

        exitCode.ShouldBe(expected);
        File.Exists(Path.Combine(_dataRoot, "memory.db")).ShouldBeFalse("nothing may launch on a bad argv");
    }
}
