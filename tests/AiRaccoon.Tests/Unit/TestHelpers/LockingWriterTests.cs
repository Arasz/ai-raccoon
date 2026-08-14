using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     Pins the capture writer's contract: every write survives, and concurrent writers neither
///     lose content nor tear the buffer — the reason a plain <c>StringWriter</c> will not do.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LockingWriterTests
{
    [Fact]
    public void EveryWriteOverload_LandsInTheBuffer()
    {
        var writer = new LockingWriter();

        writer.Write('a');
        writer.Write("bc");
        writer.WriteLine("d");

        writer.ToString().ShouldBe($"abcd{Environment.NewLine}");
    }

    [Fact]
    public async Task ConcurrentWriters_LoseNothing()
    {
        const int writers = 8;
        const int perWriter = 20_000;
        var writer = new LockingWriter();
        using var start = new Barrier(writers);

        await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < perWriter; i++)
            {
                writer.Write("ab");
            }
        }, TaskCreationOptions.LongRunning)));

        writer.ToString().Length.ShouldBe(writers * perWriter * 2);
    }
}
