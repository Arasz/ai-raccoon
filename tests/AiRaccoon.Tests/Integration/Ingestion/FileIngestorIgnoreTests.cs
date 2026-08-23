using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     `ai-raccoon.ignore` honored by `memory_ingest_directory` (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.1/§5.2): ignored files are never ingested, and the ignore file itself never produces a row.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class FileIngestorIgnoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteConnectionFactory _factory;
    private readonly FileIngestor _ingestor;
    private readonly string _testDir;

    public FileIngestorIgnoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_ignore_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = _factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        var sourceStore = new SqliteMemorySourceStore(_factory);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        _ingestor = new FileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
            new IgnoreRulesProvider(), NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            NullWatchStore.Instance, NullEmbedDrainPump.Instance);

        using var scopeCmd = _conn.CreateCommand();
        scopeCmd.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @scope);";
        scopeCmd.Parameters.AddWithValue("@key", IngestScopeKeys.ScopeGlobal);
        scopeCmd.Parameters.AddWithValue("@scope", IngestScopeKeys.Serialize([_testDir]));
        scopeCmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
        TestData.DeleteTempRoot(_testDir);
    }

    [Fact]
    public async Task IngestDirectoryAsync_IgnoreFileAtRoot_SkipsMatchedPaths_IngestsTheRest()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, IgnoreRulesProvider.FileName), "secret.md\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "secret.md"), "# do not index",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "keep.md"), "# index me",
            TestContext.Current.CancellationToken);

        var count = await _ingestor.IngestDirectoryAsync(_conn, "test_project", _testDir, null,
            TestContext.Current.CancellationToken);

        count.Indexed.ShouldBe(1);
        var paths = SelectSourceFiles();
        paths.ShouldContain(Path.Combine(_testDir, "keep.md"));
        paths.ShouldNotContain(Path.Combine(_testDir, "secret.md"));
        // The ignore file itself is never content.
        paths.ShouldNotContain(p => p.EndsWith(IgnoreRulesProvider.FileName, StringComparison.Ordinal));
    }

    /// <summary>
    ///     WP2 (docs/work/2026-08-23-post-delta-4-plan.md §WP2): the walk root's ignore file is
    ///     loaded at the walk root itself, so an ancestor's `ai-raccoon.ignore` — the one covering a
    ///     registered watch — has no effect when `memory_ingest_directory` targets a nested
    ///     subdirectory. `IngestFileAsync` already resolves this ancestor via
    ///     `ResolveIgnoreRootAsync`; the directory walk must do the same.
    /// </summary>
    [Fact]
    public async Task IngestDirectoryAsync_AncestorWatchRootIgnoreFile_SkipsMatchedPathsUnderNestedWalkRoot()
    {
        var subDir = Path.Combine(_testDir, "sub");
        var skipDir = Path.Combine(subDir, "skip");
        Directory.CreateDirectory(skipDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, IgnoreRulesProvider.FileName), "sub/skip/**\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skipDir, "secret.md"), "# do not index",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, "keep.md"), "# index me",
            TestContext.Current.CancellationToken);

        var watchStore = await RegisterWatchAsync(_testDir);
        var ingestor = CreateIngestorWithWatchStore(watchStore);

        await ingestor.IngestDirectoryAsync(_conn, "test_project", subDir, null,
            TestContext.Current.CancellationToken);

        var paths = SelectSourceFiles();
        paths.ShouldContain(Path.Combine(subDir, "keep.md"));
        paths.Count(p => p.StartsWith(skipDir, StringComparison.Ordinal)).ShouldBe(0,
            "the ancestor watch root's ignore file must exclude sub/skip/** from a walk of sub/");
    }

    /// <summary>
    ///     B1 (PR #532 review): a walk root's OWN ignore file must still be honored even when an
    ///     ancestor scope entry admits it and carries no ignore file of its own —
    ///     <see cref="ResolveIgnoreRootAsync" />'s ancestor resolution must never suppress the
    ///     walk root's own `ai-raccoon.ignore` (docs/reference/agent-memory-server.md:283-284,
    ///     docs/features/code-corpus/code-corpus.feature:73).
    /// </summary>
    [Fact]
    public async Task IngestDirectoryAsync_WalkRootOwnIgnoreFile_TakesPrecedenceOverAncestorScopeEntry()
    {
        var subDir = Path.Combine(_testDir, "src");
        Directory.CreateDirectory(subDir);
        // No ai-raccoon.ignore at _testDir (the admitting scope entry) — only at the walk root.
        await File.WriteAllTextAsync(Path.Combine(subDir, IgnoreRulesProvider.FileName), "secret.md\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, "secret.md"), "# do not index",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(subDir, "keep.md"), "# index me",
            TestContext.Current.CancellationToken);

        await _ingestor.IngestDirectoryAsync(_conn, "test_project", subDir, null,
            TestContext.Current.CancellationToken);

        var paths = SelectSourceFiles();
        paths.ShouldContain(Path.Combine(subDir, "keep.md"));
        paths.ShouldNotContain(Path.Combine(subDir, "secret.md"),
            "the walk root's own ignore file must win even when the ancestor scope entry has none");
    }

    private FileIngestor CreateIngestorWithWatchStore(IWatchStore watchStore)
    {
        var sourceStore = new SqliteMemorySourceStore(_factory);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        return new FileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
            new IgnoreRulesProvider(), NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            watchStore, NullEmbedDrainPump.Instance);
    }

    private async Task<IWatchStore> RegisterWatchAsync(string path)
    {
        var watchStore = new WatchStore(_factory);
        await watchStore.AddWatchAsync("test_project", path, 0, 0, TestContext.Current.CancellationToken);
        return watchStore;
    }

    private List<string> SelectSourceFiles()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT source_file FROM entries";
        using var reader = cmd.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
