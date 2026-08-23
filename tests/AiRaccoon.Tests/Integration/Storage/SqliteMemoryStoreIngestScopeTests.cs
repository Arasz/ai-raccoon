using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Ingest reads whatever path it is handed, so it is contained by the project's declared
///     scope exactly as memory_watch_add is — deny by default, enforced in the store so every
///     client is bound, not just the MCP tool.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreIngestScopeTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _contentRoot = TestData.CreateTempRoot("airaccoon-ingest-content");

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-ingest-scope");
    private readonly SqliteConnectionFactory _factory;
    private readonly string _outsideRoot = TestData.CreateTempRoot("airaccoon-ingest-outside");
    private readonly SqliteMemoryStore _store;

    public SqliteMemoryStoreIngestScopeTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _dataRoot, _contentRoot, _outsideRoot })
        {
            TestData.DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task IngestFile_WithNoScopeConfigured_IsRefused()
    {
        var file = await WriteFileAsync("notes.md");

        await Should.ThrowAsync<PathOutsideScopeException>(() =>
            _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestFile_InsideTheProjectScope_IsIndexed()
    {
        var file = await WriteFileAsync("notes.md");
        await SetScopeAsync(IngestScopeKeys.ScopeProject("acme"), _contentRoot);

        var indexed = await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        indexed.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task IngestFile_OutsideTheProjectScope_IsRefused()
    {
        var outside = await WriteFileAsync("notes.md");
        var scoped = Path.Combine(_contentRoot, "scoped");
        Directory.CreateDirectory(scoped);
        await SetScopeAsync(IngestScopeKeys.ScopeProject("acme"), scoped);

        await Should.ThrowAsync<PathOutsideScopeException>(() =>
            _store.IngestFileAsync("acme", outside, null, TestContext.Current.CancellationToken));
    }

    /// <summary>Containment resolves the path first, so ".." cannot walk out of the scope.</summary>
    [Fact]
    public async Task IngestFile_TraversingOutOfScope_IsRefused()
    {
        await WriteFileAsync("notes.md");
        var scoped = Path.Combine(_contentRoot, "scoped");
        Directory.CreateDirectory(scoped);
        await SetScopeAsync(IngestScopeKeys.ScopeProject("acme"), scoped);

        var traversal = Path.Combine(scoped, "..", "notes.md");

        await Should.ThrowAsync<PathOutsideScopeException>(() =>
            _store.IngestFileAsync("acme", traversal, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestFile_UnderTheGlobalScope_IsIndexed()
    {
        var file = await WriteFileAsync("notes.md");
        await SetScopeAsync(IngestScopeKeys.ScopeGlobal, _contentRoot);

        var indexed = await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        indexed.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task IngestDirectory_OutsideTheProjectScope_IsRefused()
    {
        await WriteFileAsync("notes.md");
        var scoped = Path.Combine(_contentRoot, "scoped");
        Directory.CreateDirectory(scoped);
        await SetScopeAsync(IngestScopeKeys.ScopeProject("acme"), scoped);

        await Should.ThrowAsync<PathOutsideScopeException>(() =>
            _store.IngestDirectoryAsync("acme", _contentRoot, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestDirectory_InsideTheProjectScope_IsIndexed()
    {
        await WriteFileAsync("notes.md");
        await SetScopeAsync(IngestScopeKeys.ScopeProject("acme"), _contentRoot);

        var indexed = await _store.IngestDirectoryAsync("acme", _contentRoot, null,
            TestContext.Current.CancellationToken);

        indexed.ShouldBeGreaterThan(0);
    }

    /// <summary>A non-indexable file used to return 0 before anything read it; scope still comes first.</summary>
    [Fact]
    public async Task IngestFile_NonIndexableOutsideScope_IsStillRefused()
    {
        var file = await WriteFileAsync("secrets.env");

        await Should.ThrowAsync<PathOutsideScopeException>(() =>
            _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken));
    }

    /// <summary>Pre-1.2 banks hold watch.scope.* rows; opening one must carry them over, or every ingest silently starts refusing.</summary>
    [Fact]
    public async Task LegacyWatchScopeKey_IsMigratedOnOpen()
    {
        var file = await WriteFileAsync("notes.md");
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@key, @value)",
                new { key = "watch.scope.acme", value = IngestScopeKeys.Serialize([_contentRoot]) });
        }

        // The next open runs the schema pass, which is where the migration lives.
        var indexed = await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        indexed.ShouldBeGreaterThan(0);
        (await _store.GetSettingAsync(IngestScopeKeys.ScopeProject("acme"),
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await _store.GetSettingAsync("watch.scope.acme", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>Enumeration descends into directory symlinks; a link inside scope must not smuggle out-of-scope content in.</summary>
    [Fact]
    public async Task IngestDirectory_DirectorySymlinkPointingOutsideScope_DoesNotIngestTargetContent()
    {
        await File.WriteAllTextAsync(Path.Combine(_outsideRoot, "secret.md"), "the confidential badger stash",
            TestContext.Current.CancellationToken);
        var linkPath = Path.Combine(_contentRoot, "linked");
        CreateSymlinkOrSkip(() => Directory.CreateSymbolicLink(linkPath, _outsideRoot));
        await SetScopeAsync(IngestScopeKeys.ScopeProject("acme"), _contentRoot);

        var indexed = await _store.IngestDirectoryAsync("acme", _contentRoot, null,
            TestContext.Current.CancellationToken);

        indexed.ShouldBe(0);
        var results = (await _store.SearchAsync(new SearchQuery("acme", "confidential badger stash", MinRelativeScore: 0),
            TestContext.Current.CancellationToken)).Results;
        results.ShouldBeEmpty();
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(0);
    }

    /// <summary>A file symlink's own path is textually in scope, but its target is not — the target must govern.</summary>
    [Fact]
    public async Task IngestFile_FileSymlinkPointingOutsideScope_IsRefused()
    {
        var target = Path.Combine(_outsideRoot, "secret.md");
        await File.WriteAllTextAsync(target, "the confidential badger stash", TestContext.Current.CancellationToken);
        var linkPath = Path.Combine(_contentRoot, "notes.md");
        CreateSymlinkOrSkip(() => File.CreateSymbolicLink(linkPath, target));
        await SetScopeAsync(IngestScopeKeys.ScopeProject("acme"), _contentRoot);

        await Should.ThrowAsync<PathOutsideScopeException>(() =>
            _store.IngestFileAsync("acme", linkPath, null, TestContext.Current.CancellationToken));
    }

    /// <summary>A symlink is not rejected outright — only when it resolves outside the scope.</summary>
    [Fact]
    public async Task IngestDirectory_SymlinkInsideScope_StillIngests()
    {
        var realDir = Path.Combine(_contentRoot, "real");
        Directory.CreateDirectory(realDir);
        await File.WriteAllTextAsync(Path.Combine(realDir, "notes.md"), "the quick brown fox",
            TestContext.Current.CancellationToken);
        var linkPath = Path.Combine(_contentRoot, "linked");
        CreateSymlinkOrSkip(() => Directory.CreateSymbolicLink(linkPath, realDir));
        await SetScopeAsync(IngestScopeKeys.ScopeProject("acme"), _contentRoot);

        var indexed = await _store.IngestDirectoryAsync("acme", _contentRoot, null,
            TestContext.Current.CancellationToken);

        indexed.ShouldBeGreaterThan(0);
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

    private async Task<string> WriteFileAsync(string name)
    {
        var path = Path.Combine(_contentRoot, name);
        await File.WriteAllTextAsync(path, "the quick brown fox", TestContext.Current.CancellationToken);
        return path;
    }

    private Task SetScopeAsync(string key, string directory) =>
        _store.SetSettingAsync(key, IngestScopeKeys.Serialize([directory]),
            TestContext.Current.CancellationToken);
}
