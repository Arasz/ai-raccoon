using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemorySourceStoreTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-source-store");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemorySourceStore _sourceStore;
    private readonly SqliteMemoryStore _store;

    public SqliteMemorySourceStoreTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _sourceStore = new SqliteMemorySourceStore(_factory);
        _store = new SqliteMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task ResolveOrCreate_NewSource_InsertsAndReturns()
    {
        var source = await _sourceStore.ResolveOrCreateAsync(
            SourceType.File, "docs/readme.md", "## Intro", null,
            TestContext.Current.CancellationToken);

        source.SourceType.ShouldBe(SourceType.File);
        source.SourceLocator.ShouldBe("docs/readme.md");
        source.Section.ShouldBe("## Intro");
        source.Id.ShouldBeGreaterThan(0);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM memory_source",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveOrCreate_ExistingSource_ReturnsSameId()
    {
        var first = await _sourceStore.ResolveOrCreateAsync(
            SourceType.File, "docs/readme.md", "## Intro", null,
            TestContext.Current.CancellationToken);
        var second = await _sourceStore.ResolveOrCreateAsync(
            SourceType.File, "docs/readme.md", "## Intro", null,
            TestContext.Current.CancellationToken);

        second.Id.ShouldBe(first.Id);
        second.ShouldBe(first);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM memory_source",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1, "same key must not create a duplicate row");
    }

    [Fact]
    public async Task ResolveOrCreate_SameLocatorDifferentSection_DifferentRow()
    {
        var a = await _sourceStore.ResolveOrCreateAsync(
            SourceType.File, "docs/readme.md", "## Intro", null,
            TestContext.Current.CancellationToken);
        var b = await _sourceStore.ResolveOrCreateAsync(
            SourceType.File, "docs/readme.md", "## Usage", null,
            TestContext.Current.CancellationToken);

        a.Id.ShouldNotBe(b.Id);
    }

    [Fact]
    public async Task ResolveOrCreate_FileVsTranscript_DifferentRows()
    {
        var a = await _sourceStore.ResolveOrCreateAsync(
            SourceType.File, "docs/readme.md", null, null,
            TestContext.Current.CancellationToken);
        var b = await _sourceStore.ResolveOrCreateAsync(
            SourceType.Transcript, "docs/readme.md", null, null,
            TestContext.Current.CancellationToken);

        a.Id.ShouldNotBe(b.Id);
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
