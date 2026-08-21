using AiRaccoon.Infrastructure.Chunking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     Interim `ICodeChunker` (docs/work/2026-08-21-code-search-implementation-plan.md §12.5, Wave
///     3): zero chunks until the real `CodeChunker` (WP2, engine-blocked) replaces this
///     registration; a single Information line marks the gap once per process, not once per file.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoOpCodeChunkerTests
{
    [Fact]
    public void Chunk_AnyText_ReturnsZeroChunks()
    {
        var chunker = new NoOpCodeChunker(new FakeLogger<NoOpCodeChunker>());

        chunker.Chunk("class Program { }").ShouldBeEmpty();
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsZeroChunks()
    {
        var chunker = new NoOpCodeChunker(new FakeLogger<NoOpCodeChunker>());

        chunker.Chunk(string.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void Chunk_FirstCall_LogsInformationOnce()
    {
        var logger = new FakeLogger<NoOpCodeChunker>();
        var chunker = new NoOpCodeChunker(logger);

        chunker.Chunk("class Program { }");
        chunker.Chunk("class Other { }");
        chunker.Chunk("class Third { }");

        var records = logger.Collector.GetSnapshot();
        records.Count(r => r.Id.Id == 420).ShouldBe(1);
        var record = records.Single(r => r.Id.Id == 420);
        record.Level.ShouldBe(LogLevel.Information);
        record.Message.ShouldContain("code engine wave", Case.Insensitive);
    }
}
