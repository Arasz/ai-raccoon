using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Watch;

/// <summary>
///     No-overlapping-watches against a real SQLite bank (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.2/§5.5): symlink tie-break (real filesystem, unlike WatchOverlapResolverTests' fabricated
///     paths), ResolveAndAddAsync's one-transaction atomicity — a kill-9 between the prune and the
///     register must leave either the old watches or the new watch, never an unwatched path
///     (review codereviewer MUST-FIX 7) — and its TOCTOU closure: the resolve-read and the write
///     share one lock, so a concurrent writer's commit is always visible to the next resolver (S4).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WatchPruningTests : IDisposable
{
    private const string Project = "acme";
    private readonly string _dataRoot;
    private readonly SqliteConnectionFactory _factory;

    public WatchPruningTests()
    {
        _dataRoot = TestData.CreateTempRoot("watch-pruning-tests");
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task SymlinkEquivalentPair_ExactlyOneWatchSurvives_LongestLiteralPathWins()
    {
        var real = Path.Combine(_dataRoot, "real-repo");
        Directory.CreateDirectory(real);
        var linkParent = Path.Combine(_dataRoot, "links");
        Directory.CreateDirectory(linkParent);
        var link = Path.Combine(linkParent, "link-to-repo");
        CreateSymlinkOrSkip(() => Directory.CreateSymbolicLink(link, real));

        var store = new WatchStore(_factory);
        var resolver = new WatchOverlapResolver();

        // Register the shorter literal path first (real), then the longer symlink spelling
        // second: the tie-break must keep the LONGEST literal path regardless of registration
        // order, so the longer (symlink) path must survive and the shorter one must be rejected.
        await store.AddWatchAsync(Project, real, 1, 0, TestContext.Current.CancellationToken);

        var decision = await store.ResolveAndAddAsync(Project, new WatchOverlapCandidate(link, 2), resolver,
            TestContext.Current.CancellationToken);

        // Real-path-equivalent, different literal spellings: `link` (longer literal) wins the
        // tie-break over the shorter `real` — so adding it is Accepted, pruning `real`.
        decision.Outcome.ShouldBe(WatchOverlapOutcome.Accepted);
        decision.Pruned.ShouldHaveSingleItem();
        decision.Pruned[0].Path.ShouldBe(real);

        var remaining = (await store.ListWatchesAsync(TestContext.Current.CancellationToken))
            .Where(w => w.ProjectId == Project).ToArray();
        remaining.ShouldHaveSingleItem();
        remaining[0].Path.ShouldBe(link);
    }

    /// <summary>
    ///     Locking proof against the REAL production method (not a hand-rolled stand-in): a second
    ///     connection holds SQLite's write lock (its own <c>BEGIN IMMEDIATE</c>) for the whole bank
    ///     while <see cref="WatchStore.ResolveAndAddAsync" /> is called on the store under test —
    ///     the real method must surface <c>SQLITE_BUSY</c> (via the 5s busy_timeout the connection
    ///     factory configures) rather than silently corrupting state, and the old watch + its
    ///     fingerprint must be completely unchanged afterward (kill-9-between-prune-and-register
    ///     equivalence: never an unwatched path either way).
    /// </summary>
    [Fact]
    public async Task ResolveAndAddAsync_CompetingWriteLockHeld_ThrowsSqliteBusy_AndLeavesOldWatchesIntact()
    {
        var outer = Path.Combine(_dataRoot, "repo");
        var inner = Path.Combine(outer, "src");
        Directory.CreateDirectory(inner);
        var store = new WatchStore(_factory);
        var resolver = new WatchOverlapResolver();
        await store.AddWatchAsync(Project, inner, 1, 0, TestContext.Current.CancellationToken);
        await store.UpsertFileHashAsync(Project, Path.Combine(inner, "a.md"), "hash-a", 1,
            TestContext.Current.CancellationToken);

        // Acquire SQLite's write lock from OUTSIDE the store under test, and hold it — this is
        // what actually exercises ResolveAndAddAsync's own BEGIN IMMEDIATE contention handling,
        // unlike hand-rolling the same statement sequence on a single connection.
        // Pooling=False so this connection's native handle (and its lock) is unambiguously
        // released when disposed below, not silently handed back to a pool still holding it.
        await using var competing = new SqliteConnection($"Data Source={_factory.BankPath};Pooling=False");
        await competing.OpenAsync(TestContext.Current.CancellationToken);
        await competing.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE",
            cancellationToken: TestContext.Current.CancellationToken));

        try
        {
            var busy = await Should.ThrowAsync<SqliteException>(() =>
                store.ResolveAndAddAsync(Project, new WatchOverlapCandidate(outer, 2), resolver,
                    TestContext.Current.CancellationToken));
            busy.SqliteErrorCode.ShouldBe(5, $"expected SQLITE_BUSY (5), message: {busy.Message}");
        }
        finally
        {
            await competing.ExecuteAsync(new CommandDefinition("ROLLBACK",
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var registrations = (await store.ListWatchesAsync(TestContext.Current.CancellationToken))
            .Where(w => w.ProjectId == Project).ToArray();
        // Never an unwatched path: the old watch must still be exactly as it was, nothing partial.
        registrations.ShouldHaveSingleItem();
        registrations[0].Path.ShouldBe(inner, "a busy-locked call must never have run any of its writes");
        (await store.GetFileHashAsync(Project, Path.Combine(inner, "a.md"), TestContext.Current.CancellationToken))
            .ShouldBe("hash-a", "the pruned watch's fingerprint must not have been touched by the busy-locked call");
    }

    [Fact]
    public async Task ResolveAndAddAsync_CommittedNormally_LeavesOnlyTheNewWatch_NeverBoth()
    {
        var outer = Path.Combine(_dataRoot, "repo-commit");
        var inner = Path.Combine(outer, "src");
        Directory.CreateDirectory(inner);
        var store = new WatchStore(_factory);
        var resolver = new WatchOverlapResolver();
        await store.AddWatchAsync(Project, inner, 1, 0, TestContext.Current.CancellationToken);

        await store.ResolveAndAddAsync(Project, new WatchOverlapCandidate(outer, 2), resolver,
            TestContext.Current.CancellationToken);

        var registrations = (await store.ListWatchesAsync(TestContext.Current.CancellationToken))
            .Where(w => w.ProjectId == Project).ToArray();
        registrations.ShouldHaveSingleItem();
        registrations[0].Path.ShouldBe(outer);
    }

    /// <summary>
    ///     S4 TOCTOU close, proven deterministically (no barrier/timing race): a competing
    ///     connection inserts (but does not yet commit) an OUTER watch while holding SQLite's write
    ///     lock; <see cref="WatchStore.ResolveAndAddAsync" /> is started for an INNER path (which
    ///     the outer watch would contain) while that lock is still held, then the competing
    ///     connection commits. SQLite blocks the second call's own <c>BEGIN IMMEDIATE</c> until the
    ///     first commits — so the second call can only ever resolve after the outer watch is
    ///     durable, never against the pre-lock, watch-less snapshot. Before the fix (the resolve-read
    ///     ran on its own connection, outside any transaction) this reliably observed the stale
    ///     snapshot and returned Accepted, leaving BOTH watches registered — a real violation of
    ///     no-overlapping-watches, not a hypothetical one.
    /// </summary>
    [Fact]
    public async Task ResolveAndAddAsync_ConcurrentCompetingWriter_ResolvesAgainstItsCommittedState_NeverAStaleSnapshot()
    {
        var outer = Path.Combine(_dataRoot, "concurrency-outer");
        var inner = Path.Combine(outer, "src");
        Directory.CreateDirectory(inner);
        var store = new WatchStore(_factory);
        var resolver = new WatchOverlapResolver();
        // Prime the bank's schema (factory.OpenBankAsync's EnsureAsync) before opening a raw
        // second connection directly — nothing else in this test goes through the store first.
        await store.ListWatchesAsync(TestContext.Current.CancellationToken);

        await using var competing = new SqliteConnection($"Data Source={_factory.BankPath};Pooling=False");
        await competing.OpenAsync(TestContext.Current.CancellationToken);
        await competing.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE",
            cancellationToken: TestContext.Current.CancellationToken));
        // The competing "first caller" has already decided to add the OUTER watch and inserted it,
        // but has not committed yet — a pre-transaction read by a second caller would see nothing.
        await competing.ExecuteAsync(new CommandDefinition(MemorySql.InsertWatchIfAbsent,
            new { projectId = Project, path = outer, createdAt = 1L, lastChangeTs = 0L },
            cancellationToken: TestContext.Current.CancellationToken));

        // Microsoft.Data.Sqlite's busy_timeout retry loop runs synchronously inside the "async"
        // call (SQLite itself has no async I/O) — without Task.Run this would block the test
        // thread itself, starving the COMMIT below of a chance to ever execute concurrently.
        var second = Task.Run(() => store.ResolveAndAddAsync(Project, new WatchOverlapCandidate(inner, 2),
            resolver, TestContext.Current.CancellationToken));

        // Release the competing writer's lock only now: the second call's own BEGIN IMMEDIATE can
        // only succeed once this commits, so its resolve-read can only ever observe the outer
        // watch as already present — never the pre-lock, watch-less snapshot.
        await competing.ExecuteAsync(new CommandDefinition("COMMIT",
            cancellationToken: TestContext.Current.CancellationToken));

        var decision = await second;

        decision.Outcome.ShouldBe(WatchOverlapOutcome.Rejected,
            "the second caller must resolve against the FIRST caller's committed watch, not a stale pre-transaction read");
        decision.CoveringPath.ShouldBe(outer);

        var registrations = (await store.ListWatchesAsync(TestContext.Current.CancellationToken))
            .Where(w => w.ProjectId == Project).ToArray();
        registrations.ShouldHaveSingleItem();
        registrations[0].Path.ShouldBe(outer);
    }

    /// <summary>
    ///     S4 real-race companion to the deterministic serialization proof above: two genuinely
    ///     concurrent <see cref="WatchStore.ResolveAndAddAsync" /> calls for an outer/inner
    ///     overlapping pair, fired via <see cref="Task.Run(Func{Task})" /> (not sequenced through a
    ///     held external lock) and awaited together. Timing-based, but reproduced the pre-fix
    ///     TOCTOU bug reliably (3/3 local runs, "registrations.Length" 2 instead of 1) and passes
    ///     reliably post-fix (5/5 local runs) — kept as a second, closer-to-production witness
    ///     alongside the fully deterministic one.
    /// </summary>
    [Fact]
    public async Task ResolveAndAddAsync_TwoGenuinelyConcurrentOverlappingAdds_ExactlyOneSurvives()
    {
        var outer = Path.Combine(_dataRoot, "race-outer");
        var inner = Path.Combine(outer, "src");
        Directory.CreateDirectory(inner);
        var store = new WatchStore(_factory);
        var resolver = new WatchOverlapResolver();
        await store.ListWatchesAsync(TestContext.Current.CancellationToken);

        var taskA = Task.Run(() => store.ResolveAndAddAsync(Project, new WatchOverlapCandidate(outer, 1), resolver,
            TestContext.Current.CancellationToken));
        var taskB = Task.Run(() => store.ResolveAndAddAsync(Project, new WatchOverlapCandidate(inner, 2), resolver,
            TestContext.Current.CancellationToken));
        await Task.WhenAll(taskA, taskB);

        var registrations = (await store.ListWatchesAsync(TestContext.Current.CancellationToken))
            .Where(w => w.ProjectId == Project).ToArray();
        registrations.Length.ShouldBe(1, "no-overlapping-watches: outer contains inner, only one may survive");
    }

    private static void CreateSymlinkOrSkip(Action createLink)
    {
        try
        {
            createLink();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"platform/user does not permit symlink creation: {ex.Message}");
        }
    }
}
