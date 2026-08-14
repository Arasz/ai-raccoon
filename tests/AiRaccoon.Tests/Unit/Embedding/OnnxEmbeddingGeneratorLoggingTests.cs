using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     docs/adr/0036 defense-in-depth: OnnxEmbeddingGenerator.Encode is the last-resort choke point
///     for every chunk that reaches the local engine, regardless of how it was produced, so it logs
///     when a chunk still gets truncated (EventId 414) and when a chunk's real content
///     implausibly collapses to almost no tokens under WordPiece (EventId 415, the [UNK] gap).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class OnnxEmbeddingGeneratorLoggingTests
{
    [Fact]
    public async Task GenerateAsync_ChunkExceedingTheWindow_LogsTruncation()
    {
        var logger = new FakeLogger<OnnxEmbeddingGenerator>();
        using var generator = new OnnxEmbeddingGenerator(BundledModel.ResolveModelPath(), BundledModel.ResolveVocabPath(), logger);
        var overLong = string.Join(" ", Enumerable.Range(1, 400).Select(i => $"word{i}"));

        await generator.GenerateAsync([overLong], cancellationToken: TestContext.Current.CancellationToken);

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(414);
        record.Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task GenerateAsync_ChunkWithinTheWindow_LogsNothing()
    {
        var logger = new FakeLogger<OnnxEmbeddingGenerator>();
        using var generator = new OnnxEmbeddingGenerator(BundledModel.ResolveModelPath(), BundledModel.ResolveVocabPath(), logger);

        await generator.GenerateAsync(["a short, ordinary chunk of text"], cancellationToken: TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_NewlineJoinedHashList_LogsPossibleUnknownTokenCollapse()
    {
        var logger = new FakeLogger<OnnxEmbeddingGenerator>();
        using var generator = new OnnxEmbeddingGenerator(BundledModel.ResolveModelPath(), BundledModel.ResolveVocabPath(), logger);
        var hashList = string.Join("\n", Enumerable.Range(0, 60)
            .Select(i => Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(i)))));

        await generator.GenerateAsync([hashList], cancellationToken: TestContext.Current.CancellationToken);

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(415);
        record.Level.ShouldBe(LogLevel.Warning);
    }
}
