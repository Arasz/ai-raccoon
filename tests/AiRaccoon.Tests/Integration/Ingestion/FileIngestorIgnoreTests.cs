using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
    private readonly FileIngestor _ingestor;
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly string _testDir;
    private readonly TimeProvider _timeProvider = new FakeTimeProvider();

    public FileIngestorIgnoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_ignore_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        var sourceStore = new SqliteMemorySourceStore(factory);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        _ingestor = new FileIngestor(matcher, new EntryEmbedder(TestData.CreateEmbeddingService(), _modelMigrationLease, _timeProvider),
            sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(), new IgnoreRulesProvider());

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
