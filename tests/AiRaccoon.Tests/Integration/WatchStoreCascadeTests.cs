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

    /// <summary>Real-SQLite bank under a throwaway temp DataRoot; disposed at test end.</summary>
    private sealed class Stack : IDisposable
    {
        private readonly string _dataRoot;

        public Stack()
        {
            _dataRoot = Path.Combine(Path.GetTempPath(), "ai-raccoon-watch-cascade", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataRoot);
            var options = new InfrastructureOptions
            {
                DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
            };
            var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            Store = new WatchStore(factory);
        }

        public WatchStore Store { get; }

        public string Dir(params string[] segments) => Path.Combine([_dataRoot, .. segments]);

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
