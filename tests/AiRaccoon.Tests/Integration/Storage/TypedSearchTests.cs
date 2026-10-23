using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class TypedSearchTests
{
    private sealed record Hit
    {
        public string Hash { get; init; } = "";
        public double Distance { get; init; }
    }

    [RetryFact]
    public async Task Fts_MinorityType_IsSelectedBeforeCandidateWindow()
    {
        await using var connection = await OpenAsync();
        for (var i = 0; i < 110; i++)
        {
            await SeedAsync(connection, $"excluded-{i:D3}", "file", "needle");
        }
        await SeedAsync(connection, "wanted", "memory", "needle filler filler filler filler");

        var rows = (await connection.QueryAsync<Hit>(
            MemorySql.SearchByTypes(EntryType.Memory).Replace("{filter}", "e.scope = 'project' AND e.project_id = 'acme'"),
            new { query = "needle", limit = 100, entryTypes = new[] { "memory" }, unrestrictedTypes = false })).ToList();

        rows.Select(row => row.Hash).ShouldBe(["wanted"]);
    }

    [RetryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vector_MinorityType_IsSelectedBeforeCandidateWindow(bool structure)
    {
        await using var connection = await OpenAsync();
        for (var i = 0; i < 110; i++)
        {
            await SeedAsync(connection, $"excluded-{i:D3}", "file", "unrelated",
                content: Vector(1, 0), structure: Vector(1, 0));
        }
        await SeedAsync(connection, "wanted", "memory", "minority",
            content: Vector(0.8f, 0.6f), structure: Vector(0.8f, 0.6f));

        var rows = await VectorHitsAsync(connection, EntryType.Memory, structure, 100);

        rows.Select(row => row.Hash).ShouldBe(["wanted"]);
        rows[0].Distance.ShouldBe(0.2, 0.00001);
    }

    [RetryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vector_MultipleTypePartitions_GlobalTopKUsesDistanceThenPath(bool structure)
    {
        await using var connection = await OpenAsync();
        await SeedAsync(connection, "memory-far", "memory", "far", content: Vector(0.6f, 0.8f), structure: Vector(0.6f, 0.8f));
        await SeedAsync(connection, "file-nearest", "file", "nearest", content: Vector(1, 0), structure: Vector(1, 0));
        await SeedAsync(connection, "a-file-tied", "file", "tie-a", content: Vector(0.8f, 0.6f), structure: Vector(0.8f, 0.6f));
        await SeedAsync(connection, "z-memory-tied", "memory", "tie-z", content: Vector(0.8f, 0.6f), structure: Vector(0.8f, 0.6f));

        var rows = await VectorHitsAsync(connection, EntryType.Memory | EntryType.File, structure, 3);

        rows.Select(row => row.Hash).ShouldBe(["file-nearest", "a-file-tied", "z-memory-tied"]);
    }

    [RetryTheory]
    [InlineData(EntryType.Memory, new[] { "memory" })]
    [InlineData(EntryType.File, new[] { "file" })]
    [InlineData(EntryType.Code, new string[0])]
    [InlineData(EntryType.Memory | EntryType.Code, new[] { "memory" })]
    [InlineData(EntryType.File | EntryType.Code, new[] { "file" })]
    [InlineData(EntryType.Memory | EntryType.File, new[] { "file", "unknown", "none", "memory" })]
    [InlineData(EntryTypeParameter.Default, new[] { "file", "unknown", "none", "memory" })]
    public async Task Vector_TypeSets_KeepGlobalDistanceOrderIncludingUnclassifiedForBothMemoryKinds(EntryType types, string[] expected)
    {
        await using var connection = await OpenAsync();
        await SeedAsync(connection, "memory", "memory", "needle", content: Vector(0.6f, 0.8f));
        await SeedAsync(connection, "none", "none", "needle", content: Vector(0.8f, 0.6f));
        await SeedAsync(connection, "unknown", "future-kind", "needle", content: Vector(0.9f, 0.4358899f));
        await SeedAsync(connection, "file", "file", "needle", content: Vector(1, 0));

        var rows = await VectorHitsAsync(connection, types, false, 100);

        rows.Select(row => row.Hash).ShouldBe(expected);
    }

    [RetryTheory]
    [InlineData(EntryType.Memory, new[] { "memory" })]
    [InlineData(EntryType.File, new[] { "file" })]
    [InlineData(EntryType.Code, new string[0])]
    [InlineData(EntryType.Memory | EntryType.File, new[] { "file", "memory", "none", "unknown" })]
    [InlineData(EntryTypeParameter.Default, new[] { "file", "memory", "none", "unknown" })]
    public async Task Fts_TypeSets_IncludeUnclassifiedOnlyWhenBothMemoryKindsAreAsked(EntryType types, string[] expected)
    {
        await using var connection = await OpenAsync();
        foreach (var type in new[] { "memory", "file", "none", "unknown" })
        {
            await SeedAsync(connection, type, type, "needle");
        }

        var query = new SearchQuery("acme", "needle", EntryTypes: types);
        var parameters = DynamicParameters.SearchParameters(query,
            SearchParameters.FromSources(query), FtsQueryNormalizer.BuildPlan("needle"),
            new QueryVector([]), new ContextFilter("e.project_id = @projectId",
                new Dictionary<string, object?> { ["projectId"] = "acme" }));
        var rows = await connection.QueryAsync<Hit>(MemorySql.SearchByTypes(types).Replace("{filter}", "e.project_id = @projectId"), parameters);

        rows.Select(row => row.Hash).ShouldBe(expected);
    }

    [RetryTheory]
    [InlineData("project", "project:acme", null, null)]
    [InlineData("shared", "shared", null, null)]
    [InlineData("custom", "label:acme:custom-label", "custom-label", null)]
    [InlineData("project", "workspace:codex-pr913-search", null, "codex-pr913-search")]
    public async Task Vector_TypeFilter_ComposesWithContext(string scope, string context, string? label, string? workspaceId)
    {
        await using var connection = await OpenAsync();
        await SeedAsync(connection, "wanted", "memory", "needle", scope, "acme", label, workspaceId, Vector(0.8f, 0.6f));
        await SeedAsync(connection, "excluded-type", "file", "needle", scope, "acme", label, workspaceId, Vector(1, 0));
        await SeedAsync(connection, "other-project", "memory", "needle", "project", "other", content: Vector(1, 0));
        await SeedAsync(connection, "other-workspace", "memory", "needle", "project", "acme", workspaceId: "other-workspace", content: Vector(1, 0));

        var rows = await VectorHitsAsync(connection, EntryType.Memory, false, 100, MemorySql.ContextKeyFor(context, "acme"));
        var contextFilter = ContextFilterProvider.For(context, "acme", "e.");
        var query = new SearchQuery("acme", "needle", EntryTypes: EntryType.Memory);
        var ftsParameters = DynamicParameters.SearchParameters(query, SearchParameters.FromSources(query),
            FtsQueryNormalizer.BuildPlan("needle"), QueryVector.Empty, contextFilter);
        var ftsRows = await connection.QueryAsync<Hit>(
            MemorySql.SearchByTypes(EntryType.Memory).Replace("{filter}", contextFilter.Filter), ftsParameters);
        var codeRows = await VectorHitsAsync(connection, EntryType.Code, false, 100, MemorySql.ContextKeyFor(context, "acme"));

        rows.Select(row => row.Hash).ShouldBe(["wanted"]);
        ftsRows.Select(row => row.Hash).ShouldBe(["wanted"]);
        codeRows.ShouldBeEmpty();
    }

    [RetryFact]
    public async Task Store_TypedDualVectorSearch_PreservesAlphaFusionAcrossTypes()
    {
        var root = TestData.CreateTempRoot("typed-search-store");
        await using var endpoint = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var options = new InfrastructureOptions { DataRoot = root, Rid = "osx-arm64", Scope = InstallScope.User };
            var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
            var embeddingService = TestData.CreateEmbeddingService();
            var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
                new SqliteMemorySourceStore(factory), new SingleChunkChunker(), clock, embeddingService,
                null, null, null, null, null, null, null);
            await store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key", TestContext.Current.CancellationToken);
            await TestData.ConfigureAndDrainEmbeddingAsync(store, factory, embeddingService,
                "openai", "nomic-embed-text", endpoint.BaseUrl, TestContext.Current.CancellationToken, clock);
            await store.SetSettingAsync(SearchParameterSettingsKeys.StructureAlpha, "0.25", TestContext.Current.CancellationToken);
            await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
            var queryVector = FakeEmbeddingEndpoint.VectorFor("needle");
            var same = new byte[queryVector.Length * sizeof(float)];
            Buffer.BlockCopy(queryVector, 0, same, 0, same.Length);
            var opposite = new byte[same.Length];
            var negative = queryVector.Select(value => -value).ToArray();
            Buffer.BlockCopy(negative, 0, opposite, 0, opposite.Length);
            await SeedAsync(connection, "content-memory", "memory", "needle memory", content: same, structure: opposite);
            await SeedAsync(connection, "structure-file", "file", "needle file", content: opposite, structure: same);
            await SeedAsync(connection, "unclassified", "none", "needle unknown", content: same);
            await SeedAsync(connection, "foreign", "memory", "needle", projectId: "other", content: same, structure: same);

            var query = new SearchQuery("acme", "needle", SearchScope.Project, Limit: 10,
                MinRelativeScore: 0, FtsWeight: 0, VectorWeight: 1,
                EntryTypes: EntryType.Memory | EntryType.File);
            var mixed = await store.SearchAsync(query, TestContext.Current.CancellationToken);
            var memory = await store.SearchAsync(query with { EntryTypes = EntryType.Memory }, TestContext.Current.CancellationToken);
            var all = await store.SearchAsync(query with { EntryTypes = EntryTypeParameter.Default }, TestContext.Current.CancellationToken);
            var fts = await store.SearchAsync(query with { EntryTypes = EntryType.File, FtsWeight = 1, VectorWeight = 0 }, TestContext.Current.CancellationToken);
            var fallbackFts = await store.SearchAsync(query with { Query = "needle absentword", EntryTypes = EntryType.File, FtsWeight = 1, VectorWeight = 0 }, TestContext.Current.CancellationToken);

            mixed.Results.Select(row => row.Hash).ShouldBe(["structure-file", "unclassified", "content-memory"]);
            memory.Results.Select(row => row.Hash).ShouldBe(["content-memory"]);
            all.Results.Select(row => row.Hash).ShouldBe(["structure-file", "unclassified", "content-memory"]);
            all.Results.Select(row => row.EntryKind).ShouldBe(["file", "none", "memory"]);
            fts.Results.Select(row => row.Hash).ShouldBe(["structure-file"]);
            fallbackFts.Results.Select(row => row.Hash).ShouldBe(["structure-file"]);
            mixed.Results[0].ContentCosine.ShouldNotBeNull().ShouldBe(-1, 0.00001);
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    [RetryFact]
    public async Task Store_MixedTypes_TruncatesEachModalityGloballyBeforeAlphaFusion()
    {
        var root = TestData.CreateTempRoot("typed-search-global-fusion");
        await using var endpoint = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var options = new InfrastructureOptions { DataRoot = root, Rid = "osx-arm64", Scope = InstallScope.User };
            var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
            var embeddingService = TestData.CreateEmbeddingService();
            var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
                new SqliteMemorySourceStore(factory), new SingleChunkChunker(), clock, embeddingService,
                null, null, null, null, null, null, null);
            await store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key", TestContext.Current.CancellationToken);
            await TestData.ConfigureAndDrainEmbeddingAsync(store, factory, embeddingService,
                "openai", "nomic-embed-text", endpoint.BaseUrl, TestContext.Current.CancellationToken, clock);
            await store.SetSettingAsync(SearchParameterSettingsKeys.StructureAlpha, "0.25", TestContext.Current.CancellationToken);
            await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
            var queryVector = FakeEmbeddingEndpoint.VectorFor("needle");
            var same = AtCosine(queryVector, 1);
            var similar = AtCosine(queryVector, 0.9);
            var opposite = AtCosine(queryVector, -1);
            for (var i = 0; i < 99; i++)
            {
                await SeedAsync(connection, $"filler-{i}", "memory", $"distinct filler {i}", content: same, structure: opposite);
            }
            await SeedAsync(connection, "global-winner", "memory", "global winner", content: same, structure: similar);
            await SeedAsync(connection, "per-type-winner", "file", "per type winner", content: similar, structure: same);

            var result = await store.SearchAsync(new SearchQuery("acme", "needle", SearchScope.Project,
                Limit: 1, MinRelativeScore: 0, FtsWeight: 0, VectorWeight: 1,
                EntryTypes: EntryType.Memory | EntryType.File), TestContext.Current.CancellationToken);

            result.Results.Select(row => row.Hash).ShouldBe(["global-winner"]);
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    private static byte[] AtCosine(float[] query, double cosine)
    {
        var norm = Math.Sqrt(query.Sum(value => (double)value * value));
        var normalized = query.Select(value => value / norm).ToArray();
        var orthogonal = normalized.Select((value, index) => (index == 0 ? 1 : 0) - normalized[0] * value).ToArray();
        var orthogonalNorm = Math.Sqrt(orthogonal.Sum(value => value * value));
        var values = normalized.Select((value, index) =>
            (float)(cosine * value + Math.Sqrt(1 - cosine * cosine) * orthogonal[index] / orthogonalNorm)).ToArray();
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static async Task<IReadOnlyList<Hit>> VectorHitsAsync(SqliteConnection connection, EntryType types,
        bool structure, int limit, string context = "project:acme")
    {
        var query = new SearchQuery("acme", "needle", Limit: limit / 3, EntryTypes: types);
        var parameters = DynamicParameters.VectorParameters(query,
            SearchParameters.FromSources(query), new QueryVector(Vector(1, 0)), context);
        parameters.Add("limit", limit);
        return (await connection.QueryAsync<Hit>(MemorySql.VectorSearchByTypes(types, structure), parameters)).ToList();
    }

    private static byte[] Vector(float first, float second)
    {
        var vector = new float[384];
        vector[0] = first;
        vector[1] = second;
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("PRAGMA ignore_check_constraints=ON");
        return connection;
    }

    private static async Task SeedAsync(SqliteConnection connection, string hash, string type, string value,
        string context = "project", string projectId = "acme", string? label = null, string? workspaceId = null,
        byte[]? content = null, byte[]? structure = null)
    {
        if (workspaceId is not null)
        {
            await connection.ExecuteAsync("INSERT OR IGNORE INTO workspaces(id,project_id,status,created_at) VALUES(@workspaceId,@projectId,'Active',1)", new { workspaceId, projectId });
        }
        await connection.ExecuteAsync("""
            INSERT INTO entries(hash,path,value,scope,project_id,context_label,workspace_id,created_at,updated_at,
                                entry_type,embedding,structure_embedding,embed_state)
            VALUES (@hash,@hash,@value,@context,@projectId,@label,@workspaceId,1,1,@type,@content,@structure,'pending');
            UPDATE entries SET embed_state='embedded', structure_embedding=@structure WHERE hash=@hash;
            """, new { hash, value, type, context = workspaceId is null ? context : null, projectId, label, workspaceId, content, structure });
    }
}
