using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>The watcher buffer-size setting: one global key, a 64 KB default, clamped to what the OS documents.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchBufferTests
{
    [Fact]
    public void Key_IsTheGlobalSettingTheCliWrites()
    {
        WatchConfigKeys.BufferSizeGlobal.ShouldBe("watch.buffer-size.global");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("many")]
    [InlineData("1.5")]
    public void Resolve_MissingOrUnparseable_FallsBackToTheDefault(string? raw)
    {
        WatchBuffer.Resolve(raw).ShouldBe(64 * 1024);
    }

    [Theory]
    [InlineData("16384", 16384)]
    [InlineData(" 32768 ", 32768)]
    [InlineData("4096", 4096)]
    [InlineData("65536", 65536)]
    public void Resolve_InRange_IsUsedAsGiven(string raw, int expected)
    {
        WatchBuffer.Resolve(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("1", 4096)]
    [InlineData("-5", 4096)]
    [InlineData("1048576", 65536)]
    public void Resolve_OutOfRange_ClampsToTheDocumentedBounds(string raw, int expected)
    {
        WatchBuffer.Resolve(raw).ShouldBe(expected);
    }
}
