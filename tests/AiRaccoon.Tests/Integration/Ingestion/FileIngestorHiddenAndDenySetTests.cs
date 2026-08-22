using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     Directory-ingest enumeration hardening (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.3/§3): hidden directory segments and the built-in deny set (node_modules, bin, obj, .git,
///     .venv, __pycache__, dist, build, target) are skipped, not just hidden leaf files.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class FileIngestorHiddenAndDenySetTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly FileIngestor _ingestor;
    private readonly string _testDir;

    public FileIngestorHiddenAndDenySetTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_hidden_denyset_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        var sourceStore = new SqliteMemorySourceStore(factory);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        _ingestor = TestData.NewFileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService());

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
    public async Task IngestDirectoryAsync_HiddenDirectoryAndDenySetEntries_AreSkipped_OrdinaryFilesStillIngest()
    {
        await WriteAsync(Path.Combine(_testDir, "README.md"), "# kept");
        await WriteAsync(Path.Combine(_testDir, ".git", "HEAD"), "# git internals");
        await WriteAsync(Path.Combine(_testDir, ".hidden", "notes.md"), "# dotdir");
        await WriteAsync(Path.Combine(_testDir, "node_modules", "pkg", "readme.md"), "# dependency tree");
        await WriteAsync(Path.Combine(_testDir, "bin", "artifact.md"), "# build output");
        await WriteAsync(Path.Combine(_testDir, "obj", "artifact.md"), "# build intermediate");
        await WriteAsync(Path.Combine(_testDir, "docs", "guide.md"), "# nested but ordinary");

        var count = await _ingestor.IngestDirectoryAsync(_conn, "test_project", _testDir, null,
            TestContext.Current.CancellationToken);

        // README.md + docs/guide.md only.
        count.Indexed.ShouldBe(2);

        var indexedPaths = SelectSourceFiles();
        indexedPaths.ShouldContain(Path.Combine(_testDir, "README.md"));
        indexedPaths.ShouldContain(Path.Combine(_testDir, "docs", "guide.md"));
        indexedPaths.ShouldNotContain(p => p.Contains(".git", StringComparison.Ordinal));
        indexedPaths.ShouldNotContain(p => p.Contains(".hidden", StringComparison.Ordinal));
        indexedPaths.ShouldNotContain(p => p.Contains("node_modules", StringComparison.Ordinal));
        indexedPaths.ShouldNotContain(p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        indexedPaths.ShouldNotContain(p => p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
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
