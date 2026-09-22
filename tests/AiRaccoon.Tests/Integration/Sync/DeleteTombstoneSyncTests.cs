using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
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
///     P2.1/P2.2 (F29/F30/F31/F35): every delete path writes a tombstone for the rows its own
///     predicate actually deleted, and a fact re-created after its delete outlives its tombstone.
///     Real <see cref="SqliteMemoryStore"/> deletes plus the real <see cref="SyncService"/> merge —
///     a hand-SQL tombstone replay is not a gate.
///     <para>
///         Tombstones are label-aware: each carries the deleted row's own <c>context_label</c>, so
///         a label-scoped delete only tombstones the same label on a peer while the same-label row
///         still dies, and a NULL label (label-less rows, pre-v15 tombstones) keeps its
///         whole-context reach over every label. Known model limitation, measured and accepted:
///         same-second re-create dies — timestamps are whole seconds, so a re-create with
///         <c>created_at == deleted_at</c> is deleted again, the deliberate cost of the
///         <c>&lt;=</c> age guard whose <c>&lt;</c> alternative would break same-second delete
///         propagation.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DeleteTombstoneSyncTests : IDisposable
{
    private const string Project = "acme";
    private const string ObjectKey = "delete-tombstone-sync";
    private const string Content = "delete tombstone probe content";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("delete-tombstone-sync");
    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;
    private readonly WatchStore _watchStore;
    private readonly FakeCloudStore _cloud = new();

    public DeleteTombstoneSyncTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new SingleChunkChunker(), _time,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        _watchStore = new WatchStore(_factory);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     F29: memory_delete_context wrote no tombstone, so the next sync re-inserted the row the
    ///     remote already held. Red before P2.1: the row count is 1 after the second sync.
    /// </summary>
    [RetryFact]
    public async Task DeleteContext_ThenSync_KeepsTheContextDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry = await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        var deleted = await _store.DeleteContextAsync(Project, ContextNaming.ProjectContext(Project), ct);
        deleted.ShouldBe(1);

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountEntriesAsync(_factory, entry.Hash, ct)).ShouldBe(0,
            "the context delete's tombstone must suppress the copy the first sync pushed");
        (await CountTombstonesAsync(_factory, entry.Hash, ct)).ShouldBe(1);
    }

    /// <summary>
    ///     F30 + R4: deleting one hash that lives in a workspace row and two committed buckets must
    ///     record one tombstone per committed scope it deleted — never a workspace one. The exact-set
    ///     assertion is order-independent: a single arbitrarily-ordered probe cannot produce two rows
    ///     whatever it probes first.
    /// </summary>
    [RetryFact]
    public async Task Delete_WorkspaceAndCommittedTwins_RecordsEveryCommittedScopeAndNoWorkspaceScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await BeginWorkspaceAsync(_factory, "ws-1");
        var workspace = await _store.WriteAsync(new MemoryWriteRequest(Project, Content, WorkspaceId: "ws-1"), ct);
        var project = await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        project.Hash.ShouldBe(workspace.Hash, "arrange: the workspace and committed rows share one hash");
        var custom = await _store.AddContentAsync(Project, project.Path, Content,
            ContextNaming.LabelContext(Project, "custom"), cancellationToken: ct);
        custom.Entry.Hash.ShouldBe(project.Hash, "arrange: the custom row shares the same hash");

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        (await _store.DeleteAsync(Project, project.Hash, ct)).ShouldBe(3,
            "F26: the whole-write delete reaches all three rows sharing the write's path — the two " +
            "committed twins and the workspace twin; only the committed ones are tombstoned below");

        (await TombstoneScopesAsync(_factory, project.Hash, ct)).ShouldBe(["custom", "project"],
            "one tombstone per committed scope the delete reached");
        (await CountWorkspaceTombstonesAsync(_factory, project.Hash, ct)).ShouldBe(0,
            "a workspace row's scratch content must never become a pushed tombstone");

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountEntriesAsync(_factory, project.Hash, ct)).ShouldBe(0, "both committed buckets stay deleted");
    }

    /// <summary>
    ///     The ordering-forced variant: the workspace row is inserted first so it sorts ahead of the
    ///     committed twin (the shape the old single-probe read measured). The committed scope must
    ///     still be tombstoned — on the probe design this run writes zero tombstones and the twin
    ///     resurrects on the next sync.
    /// </summary>
    [RetryFact]
    public async Task Delete_WhenTheWorkspaceRowSortsFirst_StillRecordsTheCommittedScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await BeginWorkspaceAsync(_factory, "ws-1");
        var workspace = await _store.WriteAsync(new MemoryWriteRequest(Project, Content, WorkspaceId: "ws-1"), ct);
        var committed = await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        committed.Hash.ShouldBe(workspace.Hash);

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        await _store.DeleteAsync(Project, committed.Hash, ct);

        (await TombstoneScopesAsync(_factory, committed.Hash, ct)).ShouldBe(["project"],
            "the committed twin's scope is recorded even when the workspace row is the first one stored");

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountEntriesAsync(_factory, committed.Hash, ct)).ShouldBe(0, "the committed twin stays deleted");
    }

    /// <summary>
    ///     R4 guard: the derived tombstone set must never carry a workspace scope. Without the
    ///     committed-scope filter, A's delete of its workspace+committed twin tombstones the scratch
    ///     row; the tombstone reaches the cloud and B's independently written same-hash workspace row
    ///     is deleted on B's next pull. Red on the scope-filter-less P2.1 design.
    /// </summary>
    [RetryFact]
    public async Task Delete_OfAWorkspaceTwin_DoesNotPushAWorkspaceTombstone_OrDeleteAPeersWorkspaceRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var peerFactory = NewPeerFactory("peer");
        var peerStore = NewPeerStore(peerFactory);
        await BeginWorkspaceAsync(peerFactory, "ws-peer");

        await BeginWorkspaceAsync(_factory, "ws-local");
        var local = await _store.WriteAsync(new MemoryWriteRequest(Project, Content, WorkspaceId: "ws-local"), ct);
        await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);
        var peerWorkspace = await peerStore.WriteAsync(new MemoryWriteRequest(Project, Content, WorkspaceId: "ws-peer"), ct);
        peerWorkspace.Hash.ShouldBe(local.Hash, "arrange: the peer's workspace row shares the hash independently");

        await _store.DeleteAsync(Project, local.Hash, ct);
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountWorkspaceRowsAsync(peerFactory, local.Hash, ct)).ShouldBe(1,
            "the peer's scratch row is not the deleted row and must survive the pull");
        (await CountEntriesAsync(peerFactory, local.Hash, ct)).ShouldBe(0,
            "the peer's committed twin is the deleted row and must not survive the pull");
        var cloud = await CloudTombstoneScopesAsync(local.Hash, ct);
        cloud.Workspace.ShouldBe(0, "no workspace tombstone may be pushed off-machine");
        cloud.Committed.ShouldBe(1, "the committed deletion is what sync propagates");
    }

    /// <summary>
    ///     F35: the watcher's file-delete path shares F29's shape. Drive the digest's delete leg
    ///     directly (no real FileSystemWatcher event) so the gate is deterministic.
    /// </summary>
    [RetryFact]
    public async Task WatcherDelete_ThenSync_KeepsTheFilesChunksDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var watchDir = Path.Combine(_dataRoot, "watched");
        Directory.CreateDirectory(watchDir);
        var file = Path.Combine(watchDir, "f35.md");
        await File.WriteAllTextAsync(file, Content, ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject(Project), IngestScopeKeys.Serialize([watchDir]), ct);
        await _watchStore.AddWatchAsync(Project, watchDir, 0, 0, ct);
        var executor = new WatchDigestExecutor(_store, _watchStore, _time, new IgnoreRulesProvider(),
            new Lazy<IWatchScanInitiator>(() => new NoOpWatchScanInitiator()), TestData.NewEmbedDrainPump(),
            new SqliteProjectIdsMigrationGate(_factory));
        await executor.DigestAsync(Project, watchDir, file, WatchEventKind.Changed, null, ct);

        var hash = await HashForContentAsync(_factory, ct);
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        File.Delete(file);
        await executor.DigestAsync(Project, watchDir, file, WatchEventKind.Deleted, null, ct);

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountEntriesAsync(_factory, hash, ct)).ShouldBe(0,
            "the watcher's delete must tombstone the chunks it removed");
        (await CountTombstonesAsync(_factory, hash, ct)).ShouldBe(1);
    }

    /// <summary>
    ///     K3 (F31): a fact re-created after its delete outlives its own tombstone. Red before P2.2:
    ///     the merge's apply step deletes any row matching a tombstone regardless of age.
    /// </summary>
    [RetryFact]
    public async Task ReCreatedContent_ThenSync_SurvivesItsOwnTombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry = await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await _store.DeleteAsync(Project, entry.Hash, ct);
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        _time.Advance(TimeSpan.FromSeconds(10));
        var reCreated = await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        reCreated.Hash.ShouldBe(entry.Hash, "arrange: the re-created fact carries the same content hash");

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountEntriesAsync(_factory, entry.Hash, ct)).ShouldBe(1,
            "a row created after its tombstone must not be deleted by it");
    }

    /// <summary>
    ///     P2.2 convergence, multi-replica: a peer that already holds the tombstone keeps
    ///     suppressing the re-created row through the merge's NOT EXISTS leg, which carries no age
    ///     comparison — so convergence is partial until the peer's tombstone is GC'd in a later pull
    ///     and the re-creator re-pushes. Pins the documented partial behaviour so it is a contract,
    ///     not a surprise.
    /// </summary>
    [RetryFact]
    public async Task ReCreate_OnReplicaHoldingTheTombstone_IsSuppressedUntilLaterGc()
    {
        var ct = TestContext.Current.CancellationToken;
        var peerFactory = NewPeerFactory("peer");

        var entry = await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);

        _time.Advance(TimeSpan.FromSeconds(10));
        (await _store.DeleteAsync(Project, entry.Hash, ct)).ShouldBe(1);
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);
        (await CountEntriesAsync(peerFactory, entry.Hash, ct)).ShouldBe(0, "the delete reached the peer");

        _time.Advance(TimeSpan.FromSeconds(10));
        var reCreated = await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        reCreated.Hash.ShouldBe(entry.Hash, "arrange: the re-created fact carries the same content hash");
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        (await CountEntriesAsync(_factory, entry.Hash, ct)).ShouldBe(1,
            "the re-creator's own row survives its own tombstone");

        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);
        (await CountEntriesAsync(peerFactory, entry.Hash, ct)).ShouldBe(0,
            "PARTIAL: the peer still holds the tombstone, so the merge's NOT EXISTS leg suppresses the re-created row");

        // Convergence needs both halves: the peer's next pull GCs the aged tombstone, and the
        // re-creator must push again because the peer's row-less snapshot replaced the cloud copy.
        // The peer's next pull then merges the re-created row.
        _time.Advance(TimeSpan.FromSeconds(10));
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);
        _time.Advance(TimeSpan.FromSeconds(10));
        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        _time.Advance(TimeSpan.FromSeconds(10));
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountEntriesAsync(peerFactory, entry.Hash, ct)).ShouldBe(1,
            "eventual convergence after GC and a later re-push");
    }

    /// <summary>
    ///     Label-aware tombstones: a label-scoped context delete must reach the same label on a
    ///     peer and nothing else — the peer's same-hash row under another label survives the pull,
    ///     and the peer's other-label row still arrives at the deleter (the tombstone suppresses
    ///     only its own label). Red at base: one (project, hash, scope) tombstone kills both of the
    ///     peer's rows and suppresses the other-label row everywhere.
    /// </summary>
    [RetryFact]
    public async Task LabelScopedContextDelete_KillsThePeersSameLabelRow_ButNotItsSameHashOtherLabelRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var peerFactory = NewPeerFactory("peer");
        var peerStore = NewPeerStore(peerFactory);

        var project = await _store.WriteAsync(new MemoryWriteRequest(Project, Content), ct);
        await _store.AddContentAsync(Project, project.Path, Content,
            ContextNaming.LabelContext(Project, "ctxA"), cancellationToken: ct);
        var peerSameLabel = await peerStore.AddContentAsync(Project, project.Path, Content,
            ContextNaming.LabelContext(Project, "ctxA"), cancellationToken: ct);
        var peerOtherLabel = await peerStore.AddContentAsync(Project, project.Path, Content,
            ContextNaming.LabelContext(Project, "other"), cancellationToken: ct);
        peerSameLabel.Entry.Hash.ShouldBe(project.Hash, "arrange: the peer's same-label row shares the hash");
        peerOtherLabel.Entry.Hash.ShouldBe(project.Hash, "arrange: the peer's other-label row shares the hash");

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);

        _time.Advance(TimeSpan.FromSeconds(10));
        var deleted = await _store.DeleteContextAsync(Project,
            ContextNaming.LabelContext(Project, "ctxA"), ct);
        deleted.ShouldBe(1, "arrange: the label-scoped delete reaches exactly the deleter's own label row");

        await Sync(_factory).MemorySyncAsync(Project, ObjectKey, ct);
        await Sync(peerFactory).MemorySyncAsync(Project, ObjectKey, ct);

        (await CountLabelRowsAsync(peerFactory, project.Hash, "ctxA", ct)).ShouldBe(0,
            "positive control: the same-label row is the deleted row and must die on the peer");
        (await CountLabelRowsAsync(peerFactory, project.Hash, "other", ct)).ShouldBe(1,
            "a label-scoped delete must not reach a same-hash row under a different label");
        (await CountLabelRowsAsync(_factory, project.Hash, "other", ct)).ShouldBe(1,
            "the tombstone suppresses its own label only — the peer's other-label row must still arrive");
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

    private static SqliteMemoryStore NewPeerStore(SqliteConnectionFactory factory) =>
        TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), new SingleChunkChunker(),
            new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);

    private static async Task BeginWorkspaceAsync(SqliteConnectionFactory factory, string workspaceId) =>
        await new SqliteWorkspaceStore(factory).BeginAsync(new Workspace(workspaceId, Project), FixedNow,
            TestContext.Current.CancellationToken);

    private static async Task<int> CountEntriesAsync(SqliteConnectionFactory factory, string hash,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND hash = @hash AND workspace_id IS NULL",
            new { projectId = Project, hash }, cancellationToken: ct));
    }

    private static async Task<int> CountWorkspaceRowsAsync(SqliteConnectionFactory factory, string hash,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND hash = @hash AND workspace_id IS NOT NULL",
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

    private static async Task<int> CountLabelRowsAsync(SqliteConnectionFactory factory, string hash, string label,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND hash = @hash AND context_label = @label",
            new { projectId = Project, hash, label }, cancellationToken: ct));
    }

    private static async Task<int> CountWorkspaceTombstonesAsync(SqliteConnectionFactory factory, string hash,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM sync_tombstones WHERE project_id = @projectId AND hash = @hash AND scope = 'workspace'",
            new { projectId = Project, hash }, cancellationToken: ct));
    }

    private static async Task<IReadOnlyList<string>> TombstoneScopesAsync(SqliteConnectionFactory factory,
        string hash, CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        var scopes = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT scope FROM sync_tombstones WHERE project_id = @projectId AND hash = @hash ORDER BY scope",
            new { projectId = Project, hash }, cancellationToken: ct));
        return scopes.ToList();
    }

    private static async Task<string> HashForContentAsync(SqliteConnectionFactory factory, CancellationToken ct)
    {
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                   "SELECT hash FROM entries WHERE project_id = @projectId AND value = @value LIMIT 1",
                   new { projectId = Project, value = Content }, cancellationToken: ct))
               ?? throw new InvalidOperationException("the digest wrote no row for the watched file");
    }

    private async Task<(int Workspace, int Committed)> CloudTombstoneScopesAsync(string hash, CancellationToken ct)
    {
        var stored = await _cloud.PullAsync(ObjectKey, ct);
        stored.ShouldNotBeNull("the sync cycle pushed a snapshot to the cloud");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, stored.Data, ct);
            await using var snapshot = await OpenSnapshotAsync(path, ct);
            var workspace = await snapshot.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM sync_tombstones WHERE hash = @hash AND scope = 'workspace'", new { hash });
            var committed = await snapshot.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM sync_tombstones WHERE hash = @hash AND scope IN ('project','custom','shared')",
                new { hash });
            return (workspace, committed);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
