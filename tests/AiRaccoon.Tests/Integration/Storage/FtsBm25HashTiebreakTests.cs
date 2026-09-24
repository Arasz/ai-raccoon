using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     bm25-ordered FTS results must be deterministic on an exact score tie (owner ruling: break
///     ties by row hash, matching the Python harness's <c>ORDER BY bm25(...), e.hash</c> port —
///     scripts/retrieval_tuning/llamaindex_harness/ingest.py's <c>_bank_fts_order</c>). Every seeded
///     row shares identical value/source_file/section, so entries_fts.bm25 ties exactly across all
///     of them; insertion (id) order is deliberately the reverse of hash order, so sqlite's
///     undocumented ascending-rowid tie order and the hash-ordered fix disagree on both order and
///     membership once a LIMIT cuts inside the tie.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class FtsBm25HashTiebreakTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-fts-tiebreak");
    private readonly SqliteConnectionFactory _factory;

    public FtsBm25HashTiebreakTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task SearchByFilter_OnExactBm25Ties_OrdersByHash_NotInsertionOrder()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        // Insertion (id) order: ccc, aaa, bbb -- the reverse of hash-ascending order (aaa, bbb, ccc).
        await SeedTiedRowAsync(connection, id: 1, hash: "ccc");
        await SeedTiedRowAsync(connection, id: 2, hash: "aaa");
        await SeedTiedRowAsync(connection, id: 3, hash: "bbb");

        var rows = (await connection.QueryAsync<SqliteMemoryStore.SearchRow>(new CommandDefinition(
            MemorySql.SearchByFilter.Replace("{filter}", "e.project_id = @projectId"),
            new { query = "zephyr", limit = 2, projectId = "acme" },
            cancellationToken: TestContext.Current.CancellationToken))).ToList();

        rows.Select(r => r.Hash).ShouldBe(["aaa", "bbb"],
            "an exact bm25 tie must break by hash ascending, keeping the two lexicographically smallest rows under a LIMIT that cuts inside the tie");
    }

    private static async Task SeedTiedRowAsync(SqliteConnection connection, long id, string hash) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (id, hash, path, value, source_file, section, scope, project_id, created_at, updated_at)
            VALUES (@id, @hash, 'tied.md', 'zephyr marker text', 'test.md', '', 'project', 'acme', 1700000000, 1700000000)
            """,
            new { id, hash }, cancellationToken: TestContext.Current.CancellationToken));
}
