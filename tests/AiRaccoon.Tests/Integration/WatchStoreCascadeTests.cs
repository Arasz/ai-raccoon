using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Real-SQLite coverage for WatchStore.RemoveWatchAsync's fingerprint cascade (WP5): removing
///     a watch must also delete its watch_files rows, scoped by project and by path prefix.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(WatchIntegrationCollection.Name)]
public sealed class WatchStoreCascadeTests
{
    private const string Project = "acme";
    private const string OtherProject = "beta";

    [Fact]
    public async Task RemoveWatchAsync_AlsoDeletesTheFingerprintsUnderTheWatch()
    {
        using var stack = new Stack();
        var watchPath = stack.Dir("repo");
        var nested = stack.Dir("repo", "sub", "b.md");
        var direct = stack.Dir("repo", "a.md");
        var sibling = stack.Dir("outside", "c.md");
        await stack.Store.AddWatchAsync(Project, watchPath, 1, 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, direct, "hash-a", 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, nested, "hash-b", 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, sibling, "hash-c", 1, TestContext.Current.CancellationToken);

        await stack.Store.RemoveWatchAsync(Project, watchPath, TestContext.Current.CancellationToken);

        var remaining = await stack.Store.ListFilesAsync(Project, TestContext.Current.CancellationToken);
        remaining.ShouldNotContain(direct);
        remaining.ShouldNotContain(nested);
        remaining.ShouldContain(sibling);
    }

    [Fact]
    public async Task RemoveWatchAsync_DeletesTheFingerprintForTheWatchPathItself()
    {
        using var stack = new Stack();
        var filePath = stack.Dir("readme.md");
        await stack.Store.AddWatchAsync(Project, filePath, 1, 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, filePath, "hash-a", 1, TestContext.Current.CancellationToken);

        await stack.Store.RemoveWatchAsync(Project, filePath, TestContext.Current.CancellationToken);

        var remaining = await stack.Store.ListFilesAsync(Project, TestContext.Current.CancellationToken);
        remaining.ShouldNotContain(filePath);
    }

    [Fact]
    public async Task RemoveWatchAsync_LeavesAnotherProjectsFingerprintsAlone()
    {
        using var stack = new Stack();
        var watchPath = stack.Dir("shared");
        var filePath = stack.Dir("shared", "a.md");
        await stack.Store.AddWatchAsync(Project, watchPath, 1, 1, TestContext.Current.CancellationToken);
        await stack.Store.AddWatchAsync(OtherProject, watchPath, 1, 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, filePath, "hash-a", 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(OtherProject, filePath, "hash-b", 1,
            TestContext.Current.CancellationToken);

        await stack.Store.RemoveWatchAsync(Project, watchPath, TestContext.Current.CancellationToken);

        (await stack.Store.ListFilesAsync(Project, TestContext.Current.CancellationToken)).ShouldNotContain(filePath);
        (await stack.Store.ListFilesAsync(OtherProject, TestContext.Current.CancellationToken))
            .ShouldContain(filePath);
    }

    [Fact]
    public async Task RemoveWatchAsync_WithNoFingerprints_StillRemovesTheWatch()
    {
        using var stack = new Stack();
        var watchPath = stack.Dir("empty");
        await stack.Store.AddWatchAsync(Project, watchPath, 1, 1, TestContext.Current.CancellationToken);

        await stack.Store.RemoveWatchAsync(Project, watchPath, TestContext.Current.CancellationToken);

        var watches = await stack.Store.ListWatchesAsync(TestContext.Current.CancellationToken);
        watches.ShouldNotContain(w => w.ProjectId == Project && w.Path == watchPath);
    }

    [Fact]
    public async Task RemoveWatchAsync_WithAPathContainingLikeWildcards_DoesNotOverMatch()
    {
        using var stack = new Stack();
        var watchPath = stack.Dir("foo_bar");
        var matching = stack.Dir("foo_bar", "real.md");
        // Differs from the watch path only where the literal '_' sits; an unescaped LIKE
        // pattern treats '_' as "any single character" and would wrongly match this too.
        var decoy = stack.Dir("fooXbar", "evil.md");
        await stack.Store.AddWatchAsync(Project, watchPath, 1, 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, matching, "hash-a", 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, decoy, "hash-b", 1, TestContext.Current.CancellationToken);

        await stack.Store.RemoveWatchAsync(Project, watchPath, TestContext.Current.CancellationToken);

        var remaining = await stack.Store.ListFilesAsync(Project, TestContext.Current.CancellationToken);
        remaining.ShouldNotContain(matching);
        remaining.ShouldContain(decoy);
    }

    /// <summary>
    ///     R6: RemoveWatchAsync's `BEGIN IMMEDIATE` transaction contends for the write lock with
    ///     any fingerprint write in flight at the same time — the worry is a raw `SQLITE_BUSY`
    ///     surfacing instead of being absorbed. A plain "fire both concurrently" test races timing
    ///     luck and can pass without ever hitting the lock, so this holds the write lock open on a
    ///     second connection for a fixed window: RemoveWatchAsync must still wait it out and
    ///     succeed, not throw. Timestamps confirm the remove genuinely blocks until the hold is
    ///     released (it completes ~100ms after the commit below, not before).
    /// </summary>
    [Fact]
    public async Task RemoveWatchAsync_WhileFingerprintsAreBeingWritten_Succeeds()
    {
        using var stack = new Stack();
        var watchPath = stack.Dir("busy-remove");
        await stack.Store.AddWatchAsync(Project, watchPath, 1, 1, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, stack.Dir("busy-remove", "a.md"), "hash-a", 1,
            TestContext.Current.CancellationToken);

        // Simulates a fingerprint write already in flight: holds the write lock on a separate
        // connection, exactly the contention RemoveWatchAsync's own BEGIN IMMEDIATE would meet.
        await using var writer = await stack.OpenRawAsync(TestContext.Current.CancellationToken);
        await using (var begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Task.Run: RemoveWatchAsync's own SQLite call blocks its calling thread while it waits
        // out the lock, so it must run off-thread or it would deadlock against the COMMIT below.
        var remove = Task.Run(() => stack.Store.RemoveWatchAsync(Project, watchPath, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await using (var commit = writer.CreateCommand())
        {
            commit.CommandText = "COMMIT";
            await commit.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Should.NotThrowAsync(() => remove);

        var watches = await stack.Store.ListWatchesAsync(TestContext.Current.CancellationToken);
        watches.ShouldNotContain(w => w.ProjectId == Project && w.Path == watchPath);
    }

    /// <summary>Real-SQLite bank under a throwaway temp DataRoot; disposed at test end.</summary>
    private sealed class Stack : IDisposable
    {
        private readonly string _dataRoot;
        private readonly SqliteConnectionFactory _factory;

        public Stack()
        {
            _dataRoot = Path.Combine(Path.GetTempPath(), "ai-raccoon-watch-cascade", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataRoot);
            var options = new InfrastructureOptions
            {
                DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
            };
            _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            Store = new WatchStore(_factory);
        }

        public WatchStore Store { get; }

        public string Dir(params string[] segments) => Path.Combine([_dataRoot, .. segments]);

        /// <summary>A second connection onto the same bank, for holding a lock across the store's own calls.</summary>
        public Task<Microsoft.Data.Sqlite.SqliteConnection> OpenRawAsync(CancellationToken cancellationToken = default) =>
            _factory.OpenBankAsync(cancellationToken);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dataRoot, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
