using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Watch;
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
///     Residual #4 (join-p2.md, item 4): a replace that prunes stale chunks via
///     <see cref="SqliteMemoryStore.PruneAsync" /> (<c>DeleteAllChunksForPath</c>/
///     <c>DeleteChunksForPathExcept</c>) used to delete syncable <c>entries</c> rows with no
///     tombstone — a stale chunk a watcher CHANGE removed on one replica came back on that
///     replica's next pull. Drives the digest's replace leg directly (no real
///     <c>FileSystemWatcher</c> event), same shape as <c>DeleteTombstoneSyncTests.WatcherDelete_ThenSync</c>,
///     but with a two-chunk file so a CHANGE can remove one chunk while leaving the other untouched
///     — the case <c>DeleteChunksForPathExcept</c> (not the whole-file <c>DeleteAllChunksForPath</c>)
///     actually reaches.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PruneTombstoneSyncTests : IDisposable
{
    private const string Project = "acme";
    private const string ObjectKey = "prune-tombstone-sync";
    private const string KeptParagraph = "paragraph that survives the replace";
    private const string GoneParagraph = "paragraph that is edited away";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("prune-tombstone-sync");
    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly FakeCloudStore _cloud = new();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;
    private readonly WatchStore _watchStore;

    public PruneTombstoneSyncTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        // StubChunker splits on blank lines — a two-paragraph file chunks into two rows, so a
        // replace can remove one and leave the other, the shape DeleteChunksForPathExcept reaches.
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), _time,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        _watchStore = new WatchStore(_factory);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task WatchedFileChange_RemovingOneChunk_KeepsItDeletedOnThePeer_AndSurvivesTheUnchangedChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var peerFactory = NewPeerFactory("peer");

        var watchDir = Path.Combine(_dataRoot, "watched");
        Directory.CreateDirectory(watchDir);
        var file = Path.Combine(watchDir, "doc.md");
        await File.WriteAllTextAsync(file, $"{KeptParagraph}\n\n{GoneParagraph}", ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject(Project), IngestScopeKeys.Serialize([watchDir]), ct);
        await _watchStore.AddWatchAsync(Project, watchDir, 0, 0, ct);
        var executor = new WatchDigestExecutor(_store, _watchStore, _time, new IgnoreRulesProvider(),
            new Lazy<IWatchScanInitiator>(() => new NoOpWatchScanInitiator()), TestData.NewEmbedDrainPump(),
            new SqliteProjectIdsMigrationGate(_factory));
        await executor.DigestAsync(Project, watchDir, file, WatchEventKind.Changed, null, ct);

        var keptHash = await HashForContentAsync(_factory, KeptParagraph, ct);
        var goneHash = await HashForContentAsync(_factory, GoneParagraph, ct);

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);
        (await CountEntriesAsync(peerFactory, goneHash, ct)).ShouldBe(1,
            "arrange: the peer holds a copy of the chunk that is about to be edited away");
        (await CountEntriesAsync(peerFactory, keptHash, ct)).ShouldBe(1,
            "arrange: the peer holds a copy of the chunk that stays unchanged");

        // The watched file changes: one paragraph is edited away, the other is untouched — a
        // partial replace through DeleteChunksForPathExcept, not a whole-file delete.
        await File.WriteAllTextAsync(file, KeptParagraph, ct);
        await executor.DigestAsync(Project, watchDir, file, WatchEventKind.Changed, null, ct);

        (await CountEntriesAsync(_factory, goneHash, ct)).ShouldBe(0, "arrange: the replace pruned the stale chunk locally");
        (await CountEntriesAsync(_factory, keptHash, ct)).ShouldBe(1, "arrange: the unchanged chunk survives the replace locally");

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountEntriesAsync(peerFactory, goneHash, ct)).ShouldBe(0,
            "the stale chunk a replace pruned must not resurrect on the peer's next pull");
        (await CountEntriesAsync(peerFactory, keptHash, ct)).ShouldBe(1,
            "positive control: a chunk whose text is unchanged across the replace still survives on the peer");
    }

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

    private SqliteConnectionFactory NewPeerFactory(string name)
    {
        var options = TestData.CreateInfrastructureOptions(Path.Combine(_dataRoot, name));
        return new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    private static async Task<int> CountEntriesAsync(SqliteConnectionFactory factory, string hash,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND hash = @hash AND workspace_id IS NULL",
            new { projectId = Project, hash }, cancellationToken: ct));
    }

    private static async Task<string> HashForContentAsync(SqliteConnectionFactory factory, string value,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                   "SELECT hash FROM entries WHERE project_id = @projectId AND value = @value LIMIT 1",
                   new { projectId = Project, value }, cancellationToken: ct))
               ?? throw new InvalidOperationException("the digest wrote no row for this chunk's content");
    }
}
