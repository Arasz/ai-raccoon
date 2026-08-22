using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     "Which rows belong to this project" was written out by hand in nine queries, a trigger and a
///     filter, each with a comment pointing at MemorySql.SelectSourceByHashAndProject as the
///     authority. Every one of them read `scope = 'project'`, so a context-labelled entry — which
///     ADR-0045 establishes is inside its project — was invisible to share, TTL, extraction and the
///     project listing, while memory_get and memory_delete found it. These pin the behaviour; the
///     rule itself now lives in <see cref="ProjectRows" /> (ADR-0046).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ProjectRowsTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-project-rows");
    private readonly SqliteConnectionFactory _factory;
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly SqliteMemoryStore _store;
    private readonly TimeProvider _timeProvider = new FakeTimeProvider();

    public ProjectRowsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var embedder = new EntryEmbedder(TestData.CreateEmbeddingService(), _modelMigrationLease, _timeProvider);
        var fileIngestor = new FileIngestor(new FileTypeMatcher([]), new SqliteMemorySourceStore(_factory),
            new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService(),
            NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            NullWatchStore.Instance, NullEmbedDrainPump.Instance);
        _store = new SqliteMemoryStore(_factory, new SqliteMemorySourceStore(_factory), fileIngestor,
            embedder, new FakeTimeProvider(FixedNow), NullLogger<SqliteMemoryStore>.Instance,
            new NoiseFilteringService([]), new SqliteSettingsStore(_factory), NullEmbedDrainPump.Instance);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private Task<MemoryEntry> WriteLabelledAsync(string content = "labelled fact", string label = "adr") =>
        _store.WriteAsync(new MemoryWriteRequest("acme", content, Context: label),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task SetEntryTtl_OnAContextLabelledEntry_Applies()
    {
        var entry = await WriteLabelledAsync();

        var applied = await _store.SetEntryTtlAsync("acme", entry.Hash, 30, TestContext.Current.CancellationToken);

        applied.ShouldBeTrue("memory_get resolves this hash, so memory_set_ttl must not call it unknown");
        (await ReadTtlAsync(entry.Hash)).ShouldBe(30);
    }

    /// <summary>
    ///     The disambiguation the old predicate existed for must survive: with a project row and a
    ///     custom sibling of the same hash, the TTL lands on the project row, not whichever the
    ///     engine returns first.
    /// </summary>
    [Fact]
    public async Task SetEntryTtl_WithBothAProjectRowAndACustomSibling_PrefersTheProjectRow()
    {
        const string content = "a fact written twice";
        var project = await _store.WriteAsync(new MemoryWriteRequest("acme", content),
            TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            // A custom-scoped sibling of identical content; the dedup path returns the first row, so
            // it is inserted directly to build the ambiguous state the predicate has to resolve.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES (@hash, 'sibling.md', @value, 'custom', 'acme', 'adr', 1, 1, 'pending')
                """,
                new { hash = project.Hash, value = content },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        await _store.SetEntryTtlAsync("acme", project.Hash, 7, TestContext.Current.CancellationToken);

        await using var read = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var byScope = (await read.QueryAsync<(string Scope, int? Ttl)>(new CommandDefinition(
            "SELECT scope AS Scope, ttl_days AS Ttl FROM entries WHERE hash = @hash ORDER BY scope",
            new { hash = project.Hash }, cancellationToken: TestContext.Current.CancellationToken))).ToList();

        byScope.First(r => r.Scope == "project").Ttl.ShouldBe(7, "the project row is the one that takes the TTL");
        byScope.First(r => r.Scope == "custom").Ttl.ShouldBeNull("the sibling must not also be written");
    }

    [Fact]
    public async Task Share_PromotesAContextLabelledEntry()
    {
        var entry = await WriteLabelledAsync("a durable cross-project fact");

        var shared = await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        shared.Entry.Context.ShouldBe(ContextNaming.SharedContext,
            "a context-labelled entry is inside its project and can be promoted from it");
    }

    [Fact]
    public async Task ExtractCandidates_IncludesContextLabelledEntries()
    {
        var entry = await WriteLabelledAsync("a candidate worth promoting");
        await MarkEmbeddedAsync(entry.Hash);

        var candidates = await _store.ExtractCandidatesAsync("acme", true, TestContext.Current.CancellationToken);

        candidates.Select(c => c.Hash).ShouldContain(entry.Hash,
            "the sweep and the promotion scorer must see every row inside the project");
    }

    [Fact]
    public async Task GetProjectIds_IncludesAProjectWhoseRowsAreAllContextLabelled()
    {
        await WriteLabelledAsync();

        var projects = await _store.GetProjectIdsAsync(TestContext.Current.CancellationToken);

        projects.ShouldContain("acme", "a project with only labelled rows is still a project");
    }

    /// <summary>
    ///     ADR-0023's trigger drops a promotion_queue row once nothing backs it. Its guard mirrored
    ///     the same project-only predicate, so a queue row backed solely by a labelled entry
    ///     survived the entry's deletion — the orphan the trigger exists to prevent.
    /// </summary>
    [Fact]
    public async Task DeletingTheLastContextLabelledRow_DropsItsPromotionQueueRow()
    {
        var entry = await WriteLabelledAsync("queued candidate");
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO promotion_queue (project_id, hash, path, value, source_file, score, reasons, created_at, updated_at)
                VALUES ('acme', @hash, 'p.md', 'queued candidate', NULL, 1.0, '[]', 1, 1)
                """,
                new { hash = entry.Hash }, cancellationToken: TestContext.Current.CancellationToken));
        }

        await _store.DeleteAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        await using var read = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var remaining = await read.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM promotion_queue WHERE project_id = 'acme' AND hash = @hash",
            new { hash = entry.Hash }, cancellationToken: TestContext.Current.CancellationToken));

        remaining.ShouldBe(0, "nothing backs the candidate any more, so its queue row is an orphan");
    }

    private async Task<int?> ReadTtlAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT ttl_days FROM entries WHERE hash = @hash LIMIT 1", new { hash },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task MarkEmbeddedAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'embedded' WHERE hash = @hash", new { hash },
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
