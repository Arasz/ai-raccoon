using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     docs/plans/2026-08-08-search-knn-perf.md §3.4/§3.5: the vector queries switched from
///     `WHERE {filter}` to a native vec0 KNN over the `ctx` column. These pin the
///     invariants that switch relied on: per-context isolation, no cross-scope leakage, and k larger than the partition returns the whole partition.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreVectorPartitionTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-vector-partition-tests");
    private FakeEmbeddingEndpoint _openAi = null!;
    private SqliteMemoryStore _store = null!;
    private SqliteWorkspaceStore _workspaces = null!;

    public async ValueTask InitializeAsync()
    {
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), new SingleChunkChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());
        _workspaces = new SqliteWorkspaceStore(factory);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _openAi.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    [Fact]
    public async Task Search_VectorOnly_FindsSharedScopedContent()
    {
        var shared = await _store.AddContentAsync("acme", "shared/a.md",
            "distinctive shared content about quokkas", ContextNaming.SharedContext,
            cancellationToken: TestContext.Current.CancellationToken);

        var results = await VectorOnlySearchAsync("quokkas", SearchScope.Shared);

        results.Select(r => r.Hash).ShouldBe([shared.Entry.Hash]);
    }

    [Fact]
    public async Task Search_VectorOnly_FindsProjectScopedContent()
    {
        var project = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "distinctive project content about pangolins"),
            TestContext.Current.CancellationToken);

        var results = await VectorOnlySearchAsync("pangolins", SearchScope.Project);

        results.Select(r => r.Hash).ShouldBe([project.Hash]);
    }

    [Fact]
    public async Task Search_VectorOnly_FindsWorkspaceScopedContent()
    {
        await _workspaces.BeginAsync(new Workspace("ws-1", "acme"), FixedNow, TestContext.Current.CancellationToken);
        var workspace = await _store.AddContentAsync("acme", "w.md",
            "distinctive workspace content about narwhals", ContextNaming.WorkspaceContext("ws-1"),
            cancellationToken: TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "narwhals", WorkspaceId: "ws-1", Limit: 10,
                MinRelativeScore: 0.0, FtsWeight: 0, VectorWeight: 1),
            TestContext.Current.CancellationToken);

        results.Select(r => r.Hash).ShouldBe([workspace.Entry.Hash]);
    }

    [Fact]
    public async Task Search_VectorOnly_FindsCustomLabelledContent()
    {
        var labelled = await _store.AddContentAsync("acme", "c.md",
            "distinctive custom content about axolotls", "my-label",
            cancellationToken: TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "axolotls", SearchScope.Project, ContextLabel: "my-label",
                Limit: 10, MinRelativeScore: 0.0, FtsWeight: 0, VectorWeight: 1),
            TestContext.Current.CancellationToken);

        results.Select(r => r.Hash).ShouldBe([labelled.Entry.Hash]);
    }

    [Fact]
    public async Task Search_VectorOnly_ScopeShared_DoesNotLeakProjectScopedRows()
    {
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "distinctive project content about pangolins"),
            TestContext.Current.CancellationToken);

        var results = await VectorOnlySearchAsync("pangolins", SearchScope.Shared);

        results.ShouldBeEmpty("the shared partition must never surface a project-scoped row");
    }

    [Fact]
    public async Task Search_VectorOnly_KExceedingThePartitionSize_ReturnsTheWholePartition_WithoutErroring()
    {
        // Default candidate window k = max(limit*3, 100); a 3-row partition is far smaller than
        // that, so vec0 must return the whole partition rather than erroring on an over-large k.
        var entries = new List<MemoryEntry>();
        foreach (var text in new[]
                 {
                     "alpha content about arctic foxes",
                     "beta content about arctic foxes",
                     "gamma content about arctic foxes"
                 })
        {
            entries.Add(await _store.WriteAsync(new MemoryWriteRequest("acme", text),
                TestContext.Current.CancellationToken));
        }

        var results = await VectorOnlySearchAsync("arctic foxes", SearchScope.Project, limit: 10);

        results.Count.ShouldBe(3);
        results.Select(r => r.Hash).ShouldBe([.. entries.Select(e => e.Hash)], ignoreOrder: true);
    }

    private async Task<IReadOnlyList<MemorySearchResult>> VectorOnlySearchAsync(string query, SearchScope scope,
        int limit = 10) =>
        await _store.SearchAsync(
            new SearchQuery("acme", query, scope, Limit: limit, MinRelativeScore: 0.0, FtsWeight: 0, VectorWeight: 1),
            TestContext.Current.CancellationToken);

}
