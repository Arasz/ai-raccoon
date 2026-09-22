using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Sync;

/// <summary>
///     F36: the tombstone GC pass must compare THIS bank's own receipt time against its own
///     watermark — never a remote replica's <c>deleted_at</c>, which can already read "older than
///     our last pull" the very first time we ever see it, destroying a tombstone in the same pass
///     that just merged it. A single shared <see cref="FakeTimeProvider" /> drives every replica
///     here (no clock skew) — the bug reproduces from ordinary elapsed time between syncs alone.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TombstoneReceiptTimeGcSyncTests : IDisposable
{
    private const string Project = "acme";
    private const string ObjectKey = "tombstone-receipt-time-gc";
    private const string Content = "receipt time gc probe content";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("tombstone-receipt-time-gc");
    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly FakeCloudStore _cloud = new();
    private readonly SqliteConnectionFactory _localFactory;
    private readonly SqliteMemoryStore _localStore;

    public TombstoneReceiptTimeGcSyncTests()
    {
        _localFactory = NewFactory("local");
        _localStore = NewStore(_localFactory);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     Red before the fix: replica A deletes H, but its push reaches the local bank (B) only
    ///     after B's OWN watermark has already advanced past the delete's <c>deleted_at</c> through
    ///     unrelated idle pulls. B's GC used to compare that remote clock against its own watermark
    ///     and destroy the tombstone in the very pass that merged it — so once a third replica (C),
    ///     which never learned of the delete, later pushes its still-live copy of H, B's next pull
    ///     re-inserts it with nothing left to suppress it.
    /// </summary>
    [RetryFact]
    public async Task RemoteTombstoneOlderThanTheWatermark_StillSuppressesReArrivalFromAThirdReplica()
    {
        var ct = TestContext.Current.CancellationToken;
        var aFactory = NewFactory("a");
        var aStore = NewStore(aFactory);
        var cFactory = NewFactory("c");
        var cStore = NewStore(cFactory);

        // t=0: A creates H and pushes it; B and C both pull it in, so all three legitimately hold
        // the same content before anything is deleted.
        var entry = await aStore.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        await Sync(aFactory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(cFactory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(_localFactory).MemorySyncAsync(Project, ObjectKey, ct);
        (await CountEntriesAsync(_localFactory, entry.Hash, ct)).ShouldBe(1, "arrange: B holds H before any delete");

        // t=5: A deletes H locally (deleted_at=5) but does not push yet.
        _time.Advance(TimeSpan.FromSeconds(5));
        (await aStore.DeleteAsync(Project, entry.Hash, ct)).ShouldBe(1);

        // t=10: B does an unrelated idle pull — nothing new is in the cloud yet, but B's own
        // watermark still advances past the delete's (not-yet-pushed) timestamp.
        _time.Advance(TimeSpan.FromSeconds(5));
        await Sync(_localFactory).MemorySyncAsync(Project, ObjectKey, ct);

        // t=15: A finally pushes — the cloud now carries A's tombstone (deleted_at=5).
        _time.Advance(TimeSpan.FromSeconds(5));
        await Sync(aFactory).MemorySyncAsync(Project, ObjectKey, ct);

        // t=20: B's first-ever pull of this tombstone — deleted_at(5) predates B's own watermark
        // from the t=10 idle pull. The apply step removes B's own copy of H either way; whether the
        // suppressing tombstone SURVIVES this pass is the fix under test.
        _time.Advance(TimeSpan.FromSeconds(5));
        await Sync(_localFactory).MemorySyncAsync(Project, ObjectKey, ct);
        (await CountEntriesAsync(_localFactory, entry.Hash, ct)).ShouldBe(0, "arrange: the delete reached B");

        // t=25: C, which never pulled A's delete, syncs for the first time since t=0. Its own copy
        // of H survives this pull only if B's push (above) still carried the tombstone for C to
        // learn and apply locally — otherwise C's pull sees nothing and its live copy stays intact,
        // ready to resurrect H at B next.
        _time.Advance(TimeSpan.FromSeconds(5));
        await Sync(cFactory).MemorySyncAsync(Project, ObjectKey, ct);

        // t=30: B's next pull. If B's tombstone survived t=20, B's own re-push at t=20 carried it
        // to the cloud, C's t=25 pull applied it and removed C's copy, and nothing resurrects here.
        // If B's tombstone was wrongly GC'd, B's t=20 push wiped it from the cloud, C's t=25 pull
        // found nothing to suppress its own copy, and C's own push at t=25 puts H back in the cloud
        // for B to re-merge right here.
        _time.Advance(TimeSpan.FromSeconds(5));
        await Sync(_localFactory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountEntriesAsync(_localFactory, entry.Hash, ct)).ShouldBe(0,
            "the hash must not resurrect at B merely because B's own GC destroyed the tombstone that a " +
            "third replica's still-live copy needed to keep suppressed");
    }

    /// <summary>Positive control: a tombstone that really is old — by THIS bank's own receipt time,
    /// not a remote clock — is still collected. Otherwise the fix could satisfy the gate above by
    /// simply never garbage-collecting anything.</summary>
    [RetryFact]
    public async Task TombstoneOldByLocalReceiptTime_IsStillGarbageCollected()
    {
        var ct = TestContext.Current.CancellationToken;
        var aFactory = NewFactory("a");
        var aStore = NewStore(aFactory);

        var entry = await aStore.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        await Sync(aFactory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(_localFactory).MemorySyncAsync(Project, ObjectKey, ct);

        _time.Advance(TimeSpan.FromSeconds(5));
        await aStore.DeleteAsync(Project, entry.Hash, ct);
        await Sync(aFactory).MemorySyncAsync(Project, ObjectKey, ct);

        // B receives the tombstone here — received_at is stamped at this pull's own instant.
        _time.Advance(TimeSpan.FromSeconds(5));
        await Sync(_localFactory).MemorySyncAsync(Project, ObjectKey, ct);
        (await CountTombstonesAsync(_localFactory, entry.Hash, ct)).ShouldBe(1, "arrange: B now holds the tombstone");

        // One idle pull moves B's watermark just past the tombstone's own received_at...
        _time.Advance(TimeSpan.FromSeconds(1));
        await Sync(_localFactory).MemorySyncAsync(Project, ObjectKey, ct);
        (await CountTombstonesAsync(_localFactory, entry.Hash, ct)).ShouldBe(1,
            "not yet due: the watermark this pass GCs against still predates the tombstone's own receipt");

        // ...and the next idle pull's GC step now runs against that advanced watermark.
        _time.Advance(TimeSpan.FromSeconds(1000));
        await Sync(_localFactory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountTombstonesAsync(_localFactory, entry.Hash, ct)).ShouldBe(0,
            "a tombstone genuinely old by this bank's own receipt time must still be collected");
    }

    private SqliteConnectionFactory NewFactory(string name)
    {
        var options = TestData.CreateInfrastructureOptions(Path.Combine(_dataRoot, name));
        return new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    private SqliteMemoryStore NewStore(SqliteConnectionFactory factory) =>
        TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), new SingleChunkChunker(), _time,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);

    private SyncService Sync(SqliteConnectionFactory factory) => new(_cloud,
        ct => factory.OpenBankAsync(ct), OpenSnapshotAsync, OpenSnapshotAsync, _time,
        NullLogger<SyncService>.Instance);

    private static async Task<SqliteConnection> OpenSnapshotAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(ct);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static async Task<int> CountEntriesAsync(SqliteConnectionFactory factory, string hash,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND hash = @hash AND workspace_id IS NULL",
            new { projectId = Project, hash }, cancellationToken: ct));
    }

    private static async Task<int> CountTombstonesAsync(SqliteConnectionFactory factory, string hash,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM sync_tombstones WHERE project_id = @projectId AND hash = @hash",
            new { projectId = Project, hash }, cancellationToken: ct));
    }
}
