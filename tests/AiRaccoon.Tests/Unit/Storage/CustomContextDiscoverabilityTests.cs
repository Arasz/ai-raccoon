using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     memory_write's `context` argument lands a row in the custom scope, which memory_search only
///     reads when the caller passes the same label back (SearchContexts.For — scope `all` is shared
///     + project, by design). That is workable only if the label can be recovered; measured against
///     a live server, it could not be. A written entry was in the bank, in the FTS index and
///     retrievable by memory_get, while memory_search found nothing at any scope and memory_stats
///     reported `contexts: []` — the label existed nowhere a caller could read it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CustomContextDiscoverabilityTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-custom-context");
    private readonly SqliteConnectionFactory _factory;

    public CustomContextDiscoverabilityTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private SqliteMemoryStore CreateStore()
    {
        var embedder = new EntryEmbedder(TestData.CreateEmbeddingService());
        return new SqliteMemoryStore(_factory, new SqliteMemorySourceStore(_factory),
            new FileIngestor(new FileTypeMatcher([]), embedder, new SqliteMemorySourceStore(_factory),
                new FakeTimeProvider(FixedNow)),
            embedder, new FakeTimeProvider(FixedNow), NullLogger<SqliteMemoryStore>.Instance,
            new NoiseFilteringService([]));
    }

    [Fact]
    public async Task Stats_ReportsACustomContext_SoItsLabelCanBeSearchedFor()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.WriteAsync(
            new MemoryWriteRequest("proj-1", "The team adopted the chassis pattern.", Context: "adr"), ct);

        var stats = await store.GetStatsAsync("proj-1", ct);

        stats.Contexts.ShouldContain(c => c.Contains("adr", StringComparison.Ordinal),
            "a custom context must appear in the bank's committed contexts, or its label — the only " +
            "key that reaches its rows through memory_search — cannot be recovered by any caller");
    }

    /// <summary>
    ///     The project is the isolation boundary. A context is a label *within* a project, so a
    ///     project-scoped search must reach its rows without the caller naming the label first —
    ///     otherwise `context` is a second, undocumented isolation boundary and content written
    ///     through a first-class argument is invisible to the default search.
    /// </summary>
    [Theory]
    [InlineData(SearchScope.Project)]
    [InlineData(SearchScope.All)]
    public async Task AContextLabelledWrite_IsFoundByAPlainProjectSearch(SearchScope scope)
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.WriteAsync(
            new MemoryWriteRequest("proj-1", "The team adopted the chassis pattern.", Context: "adr"), ct);

        var results = await store.SearchAsync(
            new SearchQuery("proj-1", "chassis", scope, Limit: 5, MinRelativeScore: 0.0), ct);

        results.ShouldNotBeEmpty(
            $"a write labelled with a context belongs to its project; scope {scope} must reach it");
    }

    /// <summary>Project isolation is the boundary that must hold: another project sees nothing.</summary>
    [Fact]
    public async Task AContextLabelledWrite_StaysInsideItsProject()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.WriteAsync(
            new MemoryWriteRequest("proj-1", "The team adopted the chassis pattern.", Context: "adr"), ct);

        var results = await store.SearchAsync(
            new SearchQuery("proj-2", "chassis", SearchScope.All, Limit: 5, MinRelativeScore: 0.0), ct);

        results.ShouldBeEmpty("the project is the isolation boundary and must still hold");
    }

    /// <summary>
    ///     The two counters must mean the same "this project". CountProjectEntries counted
    ///     scope='project' only while PendingCount counted every row carrying the project id, so a
    ///     bank holding one context-labelled entry reported `entries: 0, pending: 1` — measured on
    ///     a live server.
    /// </summary>
    [Fact]
    public async Task Stats_CountsContextLabelledEntries_InTheProjectTotal()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.WriteAsync(new MemoryWriteRequest("proj-1", "plain project fact"), ct);
        await store.WriteAsync(new MemoryWriteRequest("proj-1", "labelled fact", Context: "adr"), ct);

        var stats = await store.GetStatsAsync("proj-1", ct);

        stats.EntryCount.ShouldBe(2, "a context-labelled entry belongs to its project and counts in it");
    }

    /// <summary>The label recovered from stats must be the one search accepts, not a display form.</summary>
    [Fact]
    public async Task ACustomContextReportedByStats_IsSearchableUnderThatLabel()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.WriteAsync(
            new MemoryWriteRequest("proj-1", "The team adopted the chassis pattern.", Context: "adr"), ct);

        var reported = (await store.GetStatsAsync("proj-1", ct)).Contexts
            .First(c => c.Contains("adr", StringComparison.Ordinal));
        var label = reported.Contains(':') ? reported[(reported.LastIndexOf(':') + 1)..] : reported;

        var results = await store.SearchAsync(
            new SearchQuery("proj-1", "chassis", SearchScope.All, Limit: 5, MinRelativeScore: 0.0, ContextLabel: label), ct);

        results.ShouldNotBeEmpty($"the label '{label}' reported by stats must be the one search accepts");
    }
}
