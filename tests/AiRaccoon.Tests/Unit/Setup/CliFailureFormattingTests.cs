using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     UX-F7: the catch-all reformats common I/O failures into an actionable sentence naming
///     the configured --data-root, instead of leaking a raw errno string.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class CliFailureFormattingTests
{
    private const string DataRoot = "/nonexistent-scratch-xyz/bank";

    [Fact]
    public void ReadOnlyFileSystem_NamesTheDataRootAndSuggestsAWritableOne()
    {
        var ex = new IOException("Read-only file system : '/nonexistent-scratch-xyz'");

        var message = CliFailureFormatting.Format(ex, DataRoot);

        message.ShouldContain(DataRoot);
        message.ShouldContain("read-only");
        message.ShouldContain("--data-root");
        message.ShouldNotContain("Read-only file system : '");
        message.ShouldNotContain("   at "); // no stack trace text
    }

    [Fact]
    public void PermissionDenied_NamesTheDataRootAndSuggestsCheckingPermissions()
    {
        var ex = new UnauthorizedAccessException($"Access to the path '{DataRoot}' is denied.");

        var message = CliFailureFormatting.Format(ex, DataRoot);

        message.ShouldContain(DataRoot);
        message.ShouldContain("permission");
        message.ShouldNotContain("Access to the path");
    }

    [Fact]
    public void PathTooLong_NamesTheDataRootAndSuggestsAShorterOne()
    {
        var ex = new IOException("File name too long : '" + DataRoot + "'");

        var message = CliFailureFormatting.Format(ex, DataRoot);

        message.ShouldContain(DataRoot);
        message.ShouldContain("too long");
        message.ShouldContain("shorter");
    }

    [Fact]
    public void UnrecognizedFailure_FallsBackToTheExceptionMessage()
    {
        var ex = new InvalidOperationException("something else entirely");

        var message = CliFailureFormatting.Format(ex, DataRoot);

        message.ShouldBe("ai-raccoon: something else entirely");
    }
}
