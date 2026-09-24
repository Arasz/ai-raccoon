using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     Host and CLI console logging writes every level to stderr. The suite routes that stream to a
///     file, so a failed run prints its test report instead of every in-process host's log lines.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ConsoleCaptureCollection.Name)]
public sealed class TestConsoleLogTests
{
    [Fact]
    public void Stderr_OutsideACapture_LandsInTheTestLogFile()
    {
        var marker = $"stderr-marker-{Guid.NewGuid():N}";

        Console.Error.WriteLine(marker);

        ReadLog().ShouldContain(marker);
    }

    private static string ReadLog()
    {
        using var stream = new FileStream(TestConsoleLog.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
