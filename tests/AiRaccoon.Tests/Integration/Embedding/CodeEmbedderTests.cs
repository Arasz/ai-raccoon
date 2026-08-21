using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP5 — <c>CodeEmbedder</c> (docs/work/2026-08-21-code-search-implementation-plan.md §12.2
///     H5): resolves the code engine via the keyed <see cref="IEmbeddingService.CreateGenerator" />
///     seam, degrades to <see cref="QueryVector.Empty" /> when unconfigured, and refuses actionably
///     when configured but unloadable. Uses a fake <see cref="IEmbeddingService" /> throughout — no
///     real 187 MB model download in tests.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeEmbedderTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-embedder-tests");
    private SqliteConnectionFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EmbedQueryAsync_NoCodeEngineConfigured_ReturnsEmptyVector()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var vector = await embedder.EmbedQueryAsync(connection, "QuokkaFinder", TestContext.Current.CancellationToken);

        vector.IsEmpty.ShouldBeTrue("no embedding.codeModel row means code search degrades to FTS5-only");
    }

    [Fact]
    public async Task EmbedQueryAsync_Configured_ReturnsA768DimensionVector()
    {
        var fake = new FakeCodeEmbeddingService();
        var embedder = new CodeEmbedder(fake);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");

        var vector = await embedder.EmbedQueryAsync(connection, "QuokkaFinder", TestContext.Current.CancellationToken);

        vector.IsEmpty.ShouldBeFalse();
        vector.Data.Length.ShouldBe(768 * sizeof(float));
        fake.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task EmbedQueryAsync_QueryExceedsWindow_TrimsAndSetsTrimmedFlag()
    {
        var fake = new FakeCodeEmbeddingService
        {
            TrimOverride = query => query.Length > 10 ? query[..10] : query
        };
        var embedder = new CodeEmbedder(fake);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");

        var vector = await embedder.EmbedQueryAsync(connection, "a very long query well over the trim window",
            TestContext.Current.CancellationToken);

        vector.Trimmed.ShouldBeTrue();
        fake.Calls.ShouldHaveSingleItem().ShouldHaveSingleItem().ShouldBe("a very lon");
    }

    [Fact]
    public async Task EmbedQueryAsync_QueryWithinWindow_NotTrimmed()
    {
        var fake = new FakeCodeEmbeddingService();
        var embedder = new CodeEmbedder(fake);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");

        var vector = await embedder.EmbedQueryAsync(connection, "short query", TestContext.Current.CancellationToken);

        vector.Trimmed.ShouldBeFalse();
    }

    [Fact]
    public async Task EmbedQueryAsync_ConfiguredButUnloadable_ThrowsCodeEngineUnloadableException()
    {
        var fake = new FakeCodeEmbeddingService
        {
            FailOnCreateGenerator = () => new InvalidOperationException("model.onnx is not a valid ONNX file")
        };
        var embedder = new CodeEmbedder(fake);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/broken-code-engine");

        var ex = await Should.ThrowAsync<AiRaccoon.Core.Memory.Code.CodeEngineUnloadableException>(
            () => embedder.EmbedQueryAsync(connection, "QuokkaFinder", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("/models/broken-code-engine");
        ex.Message.ShouldContain("model set code local");
    }

    [Fact]
    public async Task EmbedPendingBatchAsync_NoCodeEngineConfigured_ReturnsZero_LeavesRowsPending()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");

        var processed = await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        processed.ShouldBe(0);
        var state = await connection.ExecuteScalarAsync<string>(
            "SELECT embed_state FROM code_entries WHERE id = 1");
        state.ShouldBe("pending");
    }

    [Fact]
    public async Task EmbedPendingBatchAsync_Configured_EmbedsRowsAndFlipsEmbedState()
    {
        var fake = new FakeCodeEmbeddingService();
        var embedder = new CodeEmbedder(fake);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");
        await SeedPendingCodeRowAsync(connection, id: 2, projectId: "acme");

        var processed = await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        processed.ShouldBe(2);
        var states = await connection.QueryAsync<string>("SELECT embed_state FROM code_entries ORDER BY id");
        states.ShouldAllBe(s => s == "embedded");
        var vecCount = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM vec_code");
        vecCount.ShouldBe(2L, "MarkCodeEmbedded fires vec_code_au, which inserts into vec_code");
    }

    [Fact]
    public async Task HasPendingWorkAsync_NoCodeEngineConfigured_FalseEvenWithPendingRows()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");

        var hasWork = await embedder.HasPendingWorkAsync(connection, TestContext.Current.CancellationToken);

        hasWork.ShouldBeFalse("a pending code row with no engine is legitimately unembeddable, forever");
    }

    [Fact]
    public async Task HasPendingWorkAsync_ConfiguredWithPendingRows_True()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");
        await SeedPendingCodeRowAsync(connection, id: 1, projectId: "acme");

        var hasWork = await embedder.HasPendingWorkAsync(connection, TestContext.Current.CancellationToken);

        hasWork.ShouldBeTrue();
    }

    [Fact]
    public async Task HasPendingWorkAsync_ConfiguredNoPendingRows_False()
    {
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection, "/models/code-daemon-embed-v1");

        var hasWork = await embedder.HasPendingWorkAsync(connection, TestContext.Current.CancellationToken);

        hasWork.ShouldBeFalse();
    }

    private static async Task ActivateCodeModelAsync(SqliteConnection connection, string directory) =>
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key = EmbeddingSettingsKeys.CodeModel, value = directory });

    private static async Task SeedPendingCodeRowAsync(SqliteConnection connection, long id, string projectId) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @path, 1, 1, @projectId, 1, 1)
            """,
            new { id, hash = $"hash-{id}", path = $"src/File{id}.cs", value = $"class Sample{id} {{ }}", projectId });
}
