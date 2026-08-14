using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     A failed keyword modality degrades to the vector list by design. It must not do so
///     silently: the caller sees fewer results and has no other signal that FTS5 broke.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreDegradationTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-degradation-tests");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeLogger<SqliteMemoryStore> _logger = new();
    private readonly SqliteMemoryStore _store;

    public SqliteMemoryStoreDegradationTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, _logger, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    /// <summary>
    ///     A hyphenated markdown anchor used to reach FTS5 as a bare term, which it read as a
    ///     column filter ("no such column: started"), so the keyword modality died on every
    ///     such query. The EventId-900 warning is what made it visible.
    /// </summary>
    [Fact]
    public async Task Search_WithAHyphenatedSectionAnchor_DoesNotDegrade()
    {
        await _store.AddContentAsync("acme", "notes.md", "the quick brown fox", null,
            "notes.md", "getting-started", TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "notes.md#getting-started"), TestContext.Current.CancellationToken);

        results.ShouldNotBeEmpty("the anchor's own chunk is the answer to a path query");
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Search_WhenTheKeywordIndexWorks_LogsNoDegradation()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "the quick brown fox"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "quick"), TestContext.Current.CancellationToken);

        results.ShouldNotBeEmpty();
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Level == LogLevel.Warning);
    }

    private sealed class StubChunker : IMarkdownChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
