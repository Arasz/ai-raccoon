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

namespace AiRaccoon.Tests.Unit.storage;

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
        _store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow), new StubChunker(),
            new EmbeddingService(), _logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    [Fact]
    public async Task Search_WhenTheKeywordQueryFails_LogsTheDegradation()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "the quick brown fox"),
            TestContext.Current.CancellationToken);
        // A hyphenated section anchor reaches FTS5 as a bare term, and FTS5 barewords cannot
        // contain a hyphen — the expression is a syntax error. Markdown anchors look like this.
        var results = await _store.SearchAsync(
            new SearchQuery("acme", "notes.md#getting-started"), TestContext.Current.CancellationToken);

        // The degradation itself is deliberate — the search still answers.
        results.ShouldNotBeNull();

        // One per search context: each context's keyword list degraded independently.
        var warnings = _logger.Collector.GetSnapshot()
            .Where(r => r.Level == LogLevel.Warning)
            .ToList();
        warnings.ShouldNotBeEmpty("a failed keyword modality must not degrade silently");
        warnings.ShouldAllBe(r => r.Message.Contains("Keyword search failed"));
        warnings.ShouldAllBe(r => r.Id.Id == 900);
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

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
