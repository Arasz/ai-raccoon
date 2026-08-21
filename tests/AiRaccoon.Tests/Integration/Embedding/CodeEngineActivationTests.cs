using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The code corpus's own activation transaction (§3.3 D-E9): ActivateCodeEngineAsync commits
///     embedding.codeModel/codeEngine and invalidates every embedded code row to 'pending' in ONE
///     transaction — no outbox, no relay. The vec_code_pending trigger (proven in
///     SqliteCodeStoreTests.CodeRow_RoundTrips_ThroughFtsAndVecCodeViaEmbedStateTriggers) is what
///     empties vec_code at commit; this pins that the invalidation actually reaches the trigger
///     through this call, with no drain/reconcile phase in between (unlike the memory bank).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeEngineActivationTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-code-engine-activation");
    private SqliteConnectionFactory _factory = null!;
    private SqliteCodeEngineStore _store = null!;

    public ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteCodeEngineStore(_factory, TestData.CreateEmbeddingService());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_WritesCodeModelAndCodeEngineSettingsRows()
    {
        var dir = Path.Combine(_dataRoot, "code-model");
        Directory.CreateDirectory(dir);

        var config = await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        config.Provider.ShouldBe("local");
        config.Model.ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeEngine)).ShouldBe(config.Engine);
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_MarksEmbeddedCodeRowsPending_AndEmptiesVecCodeInTheSameCommit()
    {
        await SeedAnEmbeddedCodeRowAsync();
        (await CountVecCodeRowsAsync()).ShouldBe(1, "the seed must land an embedded row before activation");

        var dir = Path.Combine(_dataRoot, "code-model");
        Directory.CreateDirectory(dir);
        await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        (await ReadCodeEmbedStateAsync(1)).ShouldBe("pending",
            "activation must invalidate every previously-embedded code row");
        (await CountVecCodeRowsAsync()).ShouldBe(0,
            "the vec_code_pending trigger must empty vec_code in the SAME commit as the settings write " +
            "— no stale-vector window, no separate reconcile phase for code");
    }

    private async Task SeedAnEmbeddedCodeRowAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (1, 'hash-1', 'src/foo.cs', 'seed row', 'src/foo.cs', 1, 1, 'acme', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken));
        var vector = EmbeddingBlob.ToBytes(Enumerable.Repeat(0.5f, 768).ToArray());
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE id = 1",
            new { embedding = vector }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string?> ReadSettingAsync(string key)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key", new { key },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string> ReadCodeEmbedStateAsync(long id)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT embed_state FROM code_entries WHERE id = @id", new { id },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> CountVecCodeRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM vec_code", cancellationToken: TestContext.Current.CancellationToken));
    }
}
