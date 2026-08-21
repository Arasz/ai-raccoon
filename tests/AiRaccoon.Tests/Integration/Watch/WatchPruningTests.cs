using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Options;
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
///     paths) and PruneAndAddAsync's one-transaction atomicity — a kill-9 between the prune and the
///     register must leave either the old watches or the new watch, never an unwatched path
///     (review codereviewer MUST-FIX 7).
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

        var existing = new[] { new WatchOverlapCandidate(real, 1) };
        var decision = resolver.Resolve(existing, new WatchOverlapCandidate(link, 2));

        // Real-path-equivalent, different literal spellings: `link` (longer literal) wins the
        // tie-break over the shorter `real` — so adding it is Accepted, pruning `real`.
        decision.Outcome.ShouldBe(WatchOverlapOutcome.Accepted);
        decision.Pruned.ShouldHaveSingleItem();
        decision.Pruned[0].Path.ShouldBe(real);

        await store.PruneAndAddAsync(Project, link, 2, [real], TestContext.Current.CancellationToken);

        var remaining = (await store.ListWatchesAsync(TestContext.Current.CancellationToken))
            .Where(w => w.ProjectId == Project).ToArray();
        remaining.ShouldHaveSingleItem();
        remaining[0].Path.ShouldBe(link);
    }

    /// <summary>
    ///     Atomicity proof for the exact transaction PruneAndAddAsync runs (review codereviewer
    ///     MUST-FIX 7): the same SQL statements it wraps in BEGIN IMMEDIATE / COMMIT
    ///     (MemorySql.DeleteWatchFilesByProjectPathCascade, MemorySql.DeleteWatch,
    ///     MemorySql.InsertWatchIfAbsent — reused, not duplicated), abandoned WITHOUT a commit
    ///     (the SQLite-level equivalent of a kill -9 landing between the prune and the register: an
    ///     uncommitted BEGIN IMMEDIATE transaction is byte-for-byte indistinguishable on reopen from
    ///     one abandoned by a hard process kill at the same point). RED: run the same statements
    ///     WITH a COMMIT (no crash) to prove the harness lands on the new state when nothing
    ///     interrupts it; GREEN below asserts the crash path instead leaves the OLD state intact —
    ///     never an unwatched path either way.
    /// </summary>
    [Fact]
    public async Task PruneAndAddTransaction_AbandonedBeforeCommit_LeavesTheOldWatchesIntact_NeverUnwatched()
    {
        var outer = Path.Combine(_dataRoot, "repo");
        var inner = Path.Combine(outer, "src");
        Directory.CreateDirectory(inner);
        var store = new WatchStore(_factory);
        await store.AddWatchAsync(Project, inner, 1, 0, TestContext.Current.CancellationToken);
        await store.UpsertFileHashAsync(Project, Path.Combine(inner, "a.md"), "hash-a", 1,
            TestContext.Current.CancellationToken);

        // Run PruneAndAddAsync's exact statement sequence directly (same MemorySql constants),
        // but crash before COMMIT: dispose the connection with the transaction still open.
        // Pooling=False so the disposed connection's native handle is truly gone afterward — a
        // pooled connection could otherwise be handed straight back out with its open transaction
        // still attached, letting the later read see its own uncommitted writes.
        await using (var connection = new SqliteConnection($"Data Source={_factory.BankPath};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE",
                cancellationToken: TestContext.Current.CancellationToken));
            var pathPrefix = LikePattern.Escape(inner) + "/%";
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.DeleteWatchFilesByProjectPathCascade,
                new { projectId = Project, path = inner, pathPrefix },
                cancellationToken: TestContext.Current.CancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.DeleteWatch,
                new { projectId = Project, path = inner },
                cancellationToken: TestContext.Current.CancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.InsertWatchIfAbsent,
                new { projectId = Project, path = outer, createdAt = 2L, lastChangeTs = 0L },
                cancellationToken: TestContext.Current.CancellationToken));
            // No COMMIT — the connection closes here with the transaction still open, exactly
            // the state a kill -9 between the last DELETE and the COMMIT would leave on disk.
        }

        var registrations = (await store.ListWatchesAsync(TestContext.Current.CancellationToken))
            .Where(w => w.ProjectId == Project).ToArray();
        // Never an unwatched path: either the old watch (this assertion) or the new one — never neither.
        registrations.ShouldHaveSingleItem();
        registrations[0].Path.ShouldBe(inner, "an abandoned transaction must roll back to the pre-crash state");
        (await store.GetFileHashAsync(Project, Path.Combine(inner, "a.md"), TestContext.Current.CancellationToken))
            .ShouldBe("hash-a", "the pruned watch's fingerprint must not have been touched by the abandoned transaction");
    }

    [Fact]
    public async Task PruneAndAddAsync_CommittedNormally_LeavesOnlyTheNewWatch_NeverBoth()
    {
        var outer = Path.Combine(_dataRoot, "repo-commit");
        var inner = Path.Combine(outer, "src");
        Directory.CreateDirectory(inner);
        var store = new WatchStore(_factory);
        await store.AddWatchAsync(Project, inner, 1, 0, TestContext.Current.CancellationToken);

        await store.PruneAndAddAsync(Project, outer, 2, [inner], TestContext.Current.CancellationToken);

        var registrations = (await store.ListWatchesAsync(TestContext.Current.CancellationToken))
            .Where(w => w.ProjectId == Project).ToArray();
        registrations.ShouldHaveSingleItem();
        registrations[0].Path.ShouldBe(outer);
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
