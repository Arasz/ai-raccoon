using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

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
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
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

    [RetryFact]
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

    [RetryFact]
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

    [RetryFact]
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
}
