using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Embedding;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Store-level embedding scenarios (docs/work/features-native-memory/native-memory.feature):
///     pluggable engines, sync/deferred embed on write, embed_pending processing, and re-embed
///     on engine change — against the real local engine and a fake OpenAI-compatible endpoint.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EmbeddingFeatureTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private SqliteConnectionFactory _factory = null!;
    private FakeEmbeddingEndpoint _openAi = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            new EmbeddingService());
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _openAi.DisposeAsync();
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    [Fact]
    public async Task Embedding_ConfigureLocal_EmbedsWritesSynchronouslyWithoutASidecar()
    {
        var config = await _store.ConfigureEmbeddingAsync("local", null, null,
            TestContext.Current.CancellationToken);

        config.Engine.ShouldBe("local:bundled");

        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "locally embedded project fact"),
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(entry.Hash);
        row.EmbedState.ShouldBe("embedded");
        row.Embedding.ShouldNotBeNull();
        EmbeddingBlob.ToFloats(row.Embedding).Length.ShouldBe(384);
        (await CountVecRowsAsync()).ShouldBe(1);
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task Embedding_ConfigureLocal_CustomModelPath_OverridesTheBundledModel()
    {
        var custom = Path.Combine(Path.GetTempPath(), "ai-raccoon-custom-model",
            Guid.NewGuid().ToString("N"), BundledModel.ModelFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.Copy(BundledModel.ResolveModelPath(), custom);
        try
        {
            var config = await _store.ConfigureEmbeddingAsync("local", custom, null,
                TestContext.Current.CancellationToken);

            config.Engine.ShouldBe($"local:{custom}");

            var entry = await _store.WriteAsync(
                new MemoryWriteRequest("acme", "embedded with a custom model path"),
                TestContext.Current.CancellationToken);

            var row = await ReadRowAsync(entry.Hash);
            row.EmbedState.ShouldBe("embedded");
            row.Embedding.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(custom)!, true);
        }
    }

    [Fact]
    public async Task Embedding_ConfigureOpenAi_RoutesWritesThroughTheBaseUrl()
    {
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "routed through an openai compatible endpoint"),
            TestContext.Current.CancellationToken);

        var request = _openAi.Requests.ShouldHaveSingleItem();
        request.Model.ShouldBe("nomic-embed-text");
        request.Inputs.ShouldBe(["routed through an openai compatible endpoint"]);

        var row = await ReadRowAsync(entry.Hash);
        row.EmbedState.ShouldBe("embedded");
        var stored = EmbeddingBlob.ToFloats(row.Embedding);
        stored[0].ShouldBe(FakeEmbeddingEndpoint.VectorFor("routed through an openai compatible endpoint")[0]);
        (await CountVecRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Embedding_WithoutConfiguration_WritesStayDeferredAndStatsReportPending()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "deferred until an engine is configured"),
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(entry.Hash);
        row.EmbedState.ShouldBe("pending");
        row.Embedding.ShouldBeNull();
        (await CountVecRowsAsync()).ShouldBe(0);
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).PendingCount.ShouldBe(1);
    }

    [Fact]
    public async Task Embedding_EmbedPending_ProcessesTheDeferredQueueAfterConfiguration()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "queued before configuration"),
            TestContext.Current.CancellationToken);

        // Configuring the engine does not vacuum the pending queue — embed_pending owns it.
        await _store.ConfigureEmbeddingAsync("local", null, null,
            TestContext.Current.CancellationToken);

        var result = await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        result.Processed.ShouldBe(1);
        result.Pending.ShouldBe(0);
        var row = await ReadRowAsync(entry.Hash);
        row.EmbedState.ShouldBe("embedded");
        row.Embedding.ShouldNotBeNull();
        (await CountVecRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Embedding_EngineChange_ReembedsPreviouslyEmbeddedEntriesWithTheNewEngine()
    {
        var first = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "fact one for engine switch"),
            TestContext.Current.CancellationToken);
        var second = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "fact two for engine switch"),
            TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("local", null, null,
            TestContext.Current.CancellationToken);
        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        var localVectors = (await ReadRowAsync(first.Hash), await ReadRowAsync(second.Hash));
        localVectors.Item1.EmbedState.ShouldBe("embedded");

        // New engine: any OpenAI-compatible provider (the fake endpoint).
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        var reembedded = (await ReadRowAsync(first.Hash), await ReadRowAsync(second.Hash));
        reembedded.Item1.EmbedState.ShouldBe("embedded");
        reembedded.Item2.EmbedState.ShouldBe("embedded");
        EmbeddingBlob.ToFloats(reembedded.Item1.Embedding)[0].ShouldBe(
            FakeEmbeddingEndpoint.VectorFor("fact one for engine switch")[0]);
        EmbeddingBlob.ToFloats(reembedded.Item2.Embedding)[0].ShouldBe(
            FakeEmbeddingEndpoint.VectorFor("fact two for engine switch", 1)[0]);
        (await CountVecRowsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Embedding_Configure_SameEngineDoesNotReembed()
    {
        await _store.ConfigureEmbeddingAsync("local", null, null,
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "stable fact"),
            TestContext.Current.CancellationToken);

        await _store.ConfigureEmbeddingAsync("local", null, null,
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync((await _store.ListContextAsync("acme", "project:acme",
            TestContext.Current.CancellationToken)).Single().Hash);
        row.EmbedState.ShouldBe("embedded");
        (await CountVecRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Embedding_EmbedPending_DistinctHeadingPathsAreEmbeddedOncePerBatch()
    {
        const string heading = """
                               # Guide

                               ## Setup


                               """;
        await _store.WriteAsync(new MemoryWriteRequest("acme", heading + "Step one."),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest("acme", heading + "Step two."),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest("acme", heading + "Step three."),
            TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl,
            TestContext.Current.CancellationToken);

        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        // One request for the three content values, one for the single distinct heading path —
        // the three chunks share "Guide > Setup", so the structure request must not repeat it.
        _openAi.Requests.Count.ShouldBe(2);
        _openAi.Requests[0].Inputs.Count.ShouldBe(3);
        _openAi.Requests[1].Inputs.ShouldBe(["Guide > Setup"]);
        (await CountVecStructureRowsAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Embedding_ChunkWithNoHeading_WritesTheEmptySentinelAndNoVecStructureRow()
    {
        await _store.ConfigureEmbeddingAsync("local", null, null, TestContext.Current.CancellationToken);

        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "Just a plain paragraph, no headings anywhere."),
            TestContext.Current.CancellationToken);

        // '' marks "processed, no heading"; NULL means "not yet processed" and is the heal
        // candidate predicate. Writing the sentinel is what releases a headingless row from the
        // window — leaving it NULL would pin the window on that row forever.
        var row = await ReadRowAsync(entry.Hash);
        row.HeadingPath.ShouldBe("");
        row.StructureEmbedding.ShouldBeNull();
        (await CountVecStructureRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Embedding_HealingPass_PopulatesStructureForABankEmbeddedWithoutIt()
    {
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl,
            TestContext.Current.CancellationToken);
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "# Deployment guide\n\n## Rollback\n\nRestore the previous snapshot."),
            TestContext.Current.CancellationToken);
        await SimulatePreWp5ShapeAsync(entry.Hash);
        (await CountVecStructureRowsAsync()).ShouldBe(0, "the pre-WP5 simulation must have actually erased the structure row");

        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(entry.Hash);
        row.HeadingPath.ShouldBe("Deployment guide > Rollback");
        row.StructureEmbedding.ShouldNotBeNull();
        (await CountVecStructureRowsAsync()).ShouldBe(1);
        (await CountVecStructureRowsAsync()).ShouldBe(await CountStructuredEntriesAsync("acme"),
            "every structure_embedding row must have a matching vec_structure row after a heal");
    }

    [Fact]
    public async Task Embedding_WriteWithHeading_IssuesExactlyOneGeneratorCall()
    {
        // A per-chunk ingest loop (FileIngestor) calls this same synchronous-write path once per
        // chunk; two generator calls per row here means a 100-chunk document makes 200 inferences.
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl,
            TestContext.Current.CancellationToken);

        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "# Deployment guide\n\n## Rollback\n\nRestore the previous snapshot."),
            TestContext.Current.CancellationToken);

        _openAi.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Embedding_HealingPass_HeadinglessRowsLeaveTheCandidateWindowSoLaterRowsHeal()
    {
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl,
            TestContext.Current.CancellationToken);

        // The heal batch size is 32 and the first 32 rows by id are headingless, so they would pin
        // the window if processing left heading_path NULL. The '' sentinel is what advances it.
        for (var i = 0; i < 32; i++)
        {
            await _store.WriteAsync(new MemoryWriteRequest("acme", $"Plain fact number {i}, no headings anywhere."),
                TestContext.Current.CancellationToken);
        }

        var headed = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "# Deployment guide\n\n## Rollback\n\nRestore the previous snapshot."),
            TestContext.Current.CancellationToken);
        await SimulatePreWp5ShapeForProjectAsync("acme");

        // The heal pass loops internally, so one call already covers both the headingless batch
        // and the headed row past it; the second call re-proves idempotence over the same bank.
        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);
        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(headed.Hash);
        row.HeadingPath.ShouldBe("Deployment guide > Rollback",
            "a headed row past the first heal batch must still heal once the headingless rows ahead of it stop pinning the window");
        row.StructureEmbedding.ShouldNotBeNull();
    }

    [Fact]
    public async Task Embedding_HealingPass_SecondCallIsANoOp()
    {
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl,
            TestContext.Current.CancellationToken);
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "# Deployment guide\n\n## Rollback\n\nRestore the previous snapshot."),
            TestContext.Current.CancellationToken);
        await SimulatePreWp5ShapeAsync(entry.Hash);
        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);
        var requestsAfterFirstHeal = _openAi.Requests.Count;

        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        _openAi.Requests.Count.ShouldBe(requestsAfterFirstHeal, "a healed row must not be re-embedded by a later pass");
    }

    [Fact]
    public async Task Embedding_HealingPass_OneCallHealsMoreThanOneBatchOfCandidates()
    {
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl,
            TestContext.Current.CancellationToken);

        // More than 2x the heal batch size (32), so a single EmbedPendingAsync call must loop
        // the heal pass across several internal batches to heal every row.
        const int rowCount = 70;
        for (var i = 0; i < rowCount; i++)
        {
            await _store.WriteAsync(
                new MemoryWriteRequest("acme", $"# Doc {i}\n\n## Section\n\nBody paragraph number {i}."),
                TestContext.Current.CancellationToken);
        }

        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);
        await SimulatePreWp5ShapeForProjectAsync("acme");
        (await CountVecStructureRowsAsync()).ShouldBe(0, "the pre-WP5 simulation must have actually erased every structure row");

        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        (await CountVecStructureRowsAsync()).ShouldBe(rowCount,
            "one call must heal every candidate, not just the first heal batch");
        (await CountHealOpenRowsAsync("acme")).ShouldBe(0,
            "no row may be left with heading_path still NULL after one healing call");
    }

    [Fact]
    public async Task Embedding_EmbedPending_SmallLimitBoundsTheHealPassToTheRemainingBudget()
    {
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl,
            TestContext.Current.CancellationToken);

        const int rowCount = 70;
        for (var i = 0; i < rowCount; i++)
        {
            await _store.WriteAsync(
                new MemoryWriteRequest("acme", $"# Doc {i}\n\n## Section\n\nBody paragraph number {i}."),
                TestContext.Current.CancellationToken);
        }

        await SimulatePreWp5ShapeForProjectAsync("acme");
        _openAi.Requests.Clear();

        await _store.EmbedPendingAsync("acme", 1, TestContext.Current.CancellationToken);

        _openAi.Requests.Count.ShouldBe(1,
            "a limit:1 call must bound the heal pass to the remaining budget, not walk the whole backlog");
        (await CountHealOpenRowsAsync("acme")).ShouldBe(rowCount - 1,
            "only the budgeted number of heal candidates may be healed by a limit-bounded call");
    }

    [Fact]
    public async Task Embedding_Delete_RemovesTheVectorRowToo()
    {
        await _store.ConfigureEmbeddingAsync("local", null, null,
            TestContext.Current.CancellationToken);
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "fact with a vector"),
            TestContext.Current.CancellationToken);
        (await CountVecRowsAsync()).ShouldBe(1);

        await _store.DeleteAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        (await CountVecRowsAsync()).ShouldBe(0);
    }

    private async Task<EntryRow> ReadRowAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<EntryRow>(
            new CommandDefinition(
                "SELECT embed_state AS EmbedState, embedding AS Embedding, heading_path AS HeadingPath, " +
                "structure_embedding AS StructureEmbedding FROM entries WHERE hash = @hash",
                new { hash }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> CountVecRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT count(*) FROM vec_entries",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> CountVecStructureRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT count(*) FROM vec_structure",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> CountStructuredEntriesAsync(string projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM entries WHERE project_id = @projectId AND structure_embedding IS NOT NULL",
                new { projectId }, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Rows still open for the structure heal pass, mirroring SelectStructureHealCandidates.</summary>
    private async Task<int> CountHealOpenRowsAsync(string projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM entries WHERE embed_state = 'embedded' AND heading_path IS NULL " +
                "AND structure_embedding IS NULL AND project_id = @projectId",
                new { projectId }, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Resets one entry to the pre-structure-writer shape (content embedded, no structure; see
    ///     docs/plans/2026-08-08-search-knn-perf.md §3.6) to test the healing pass.</summary>
    private async Task SimulatePreWp5ShapeAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var id = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT id FROM entries WHERE hash = @hash", new { hash },
                cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE entries SET heading_path = NULL, structure_embedding = NULL WHERE id = @id",
                new { id }, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(
            new CommandDefinition("DELETE FROM vec_structure WHERE rowid = @id", new { id },
                cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Resets every row in a project to the pre-structure-writer shape
    ///     (docs/plans/2026-08-08-search-knn-perf.md §3.6), for a multi-row healing test.</summary>
    private async Task SimulatePreWp5ShapeForProjectAsync(string projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE entries SET heading_path = NULL, structure_embedding = NULL WHERE project_id = @projectId",
                new { projectId }, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM vec_structure WHERE rowid IN (SELECT id FROM entries WHERE project_id = @projectId)",
                new { projectId }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot();

    private sealed class EntryRow
    {
        public string EmbedState { get; set; } = "";

        public byte[]? Embedding { get; set; }

        public string? HeadingPath { get; set; }

        public byte[]? StructureEmbedding { get; set; }
    }
}
