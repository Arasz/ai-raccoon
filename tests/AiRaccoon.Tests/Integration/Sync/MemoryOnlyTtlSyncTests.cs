using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Sync;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemoryOnlyTtlSyncTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("memory-only-ttl-sync");
    public void Dispose() => TestData.DeleteTempRoot(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pull_NormalizesFileTtlAndKeepsTranscriptTtl(bool oldPeer)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = TestData.CreateInfrastructureOptions(_root);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var bank = new SyncTestBank(factory.BankPath);
        var remotePath = Path.Combine(_root, "remote.db");
        await using (var remote = await bank.CreateAndOpenAsync(remotePath, ct))
        {
            await remote.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries(hash,path,value,source_file,scope,project_id,created_at,updated_at,ttl_days,entry_type)
                VALUES ('file','docs/a.md','file body','docs/a.md','project','acme',1,1,11,'file'),
                       ('transcript','synthetic.md','session body','hermes/session','project','acme',1,1,13,'memory'),
                       ('declared-memory','same.md','agent body','same.md','project','acme',1,1,17,'memory');
                """, cancellationToken: ct));
            if (oldPeer)
            {
                await remote.ExecuteAsync(new CommandDefinition("ALTER TABLE entries DROP COLUMN entry_type", cancellationToken: ct));
            }
        }
        var cloud = new FakeCloudStore();
        cloud.Set("object", await File.ReadAllBytesAsync(remotePath, ct));

        var service = new SyncService(cloud, factory.OpenBankAsync, bank.OpenSnapshotAsync,
            bank.OpenReadOnlyAsync, TimeProvider.System, NullLogger<SyncService>.Instance);
        var result = await service.MemorySyncAsync("acme", "object", ct);

        result.Received.ShouldBe(3);
        await using var local = await factory.OpenBankAsync(ct);
        var rows = (await local.QueryAsync<TtlRow>(new CommandDefinition(
            "SELECT hash AS Hash, entry_type AS EntryType, ttl_days AS TtlDays, source_id AS SourceId FROM entries ORDER BY hash",
            cancellationToken: ct))).ToDictionary(row => row.Hash);
        rows["file"].TtlDays.ShouldBeNull();
        rows["file"].EntryType.ShouldBe("file");
        rows["transcript"].TtlDays.ShouldBe(13);
        rows["transcript"].EntryType.ShouldBe("memory");
        rows["transcript"].SourceId.ShouldNotBeNull();
        rows["declared-memory"].TtlDays.ShouldBe(oldPeer ? null : 17);
        rows["declared-memory"].EntryType.ShouldBe(oldPeer ? "file" : "memory");
        (await local.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT source_type FROM memory_source WHERE id = @id",
            new { id = rows["transcript"].SourceId }, cancellationToken: ct))).ShouldBe("transcript");
        await local.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embedding = @vector, structure_embedding = @vector, embed_state = 'embedded'",
            new { vector = EmbeddingBlob.ToBytes(Enumerable.Repeat(0.1f, 384).ToArray()) }, cancellationToken: ct));
        foreach (var table in new[] { "vec_entries", "vec_structure" })
        {
            var placements = (await local.QueryAsync<VectorPlacement>(new CommandDefinition(
                $"SELECT e.hash AS Hash, v.type AS Kind FROM {table} v JOIN entries e ON e.id = v.rowid",
                cancellationToken: ct))).ToDictionary(row => row.Hash, row => row.Kind);
            placements.Count.ShouldBe(3);
            placements["file"].ShouldBe("file");
            placements["transcript"].ShouldBe("memory");
            placements["declared-memory"].ShouldBe(oldPeer ? "file" : "memory");
        }
    }

    private sealed record VectorPlacement(string Hash, string Kind);

    private sealed record TtlRow(string Hash, string EntryType, long? TtlDays, long? SourceId);
}
