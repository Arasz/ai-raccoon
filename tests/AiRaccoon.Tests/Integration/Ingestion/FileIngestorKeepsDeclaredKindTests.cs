using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>A plain re-ingest keeps the declared kind and TTL of the path's row; a first ingest takes the kind from the path shape.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class FileIngestorKeepsDeclaredKindTests : IDisposable
{
    private const string Project = "acme";
    private readonly SqliteConnection _conn;
    private readonly FileIngestor _ingestor;
    private readonly string _dir;

    public FileIngestorKeepsDeclaredKindTests()
    {
        _dir = TestData.CreateTempRoot("airaccoon-declared-kind");
        var opts = new InfrastructureOptions { DataRoot = _dir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        _ingestor = new FileIngestor(matcher, new SqliteMemorySourceStore(factory), TimeProvider.System,
            TestData.CreateEmbeddingService(), NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance,
            NullCodeIngestor.Instance, NullWatchStore.Instance, NullEmbedDrainPump.Instance, new IndexableFileWalk(),
            TestData.SharedScope, new EntryKindClassifier());

        using var scope = _conn.CreateCommand();
        scope.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @scope);";
        scope.Parameters.AddWithValue("@key", IngestScopeKeys.ScopeGlobal);
        scope.Parameters.AddWithValue("@scope", IngestScopeKeys.Serialize([_dir]));
        scope.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
        TestData.DeleteTempRoot(_dir);
    }

    [RetryFact]
    public async Task IngestFileAsync_FirstIngest_StoresAFileRowWithNoTtl()
    {
        var path = await WriteAsync("first.md", "# First\n\nbody one");

        await _ingestor.IngestFileAsync(_conn, Project, path, null, TestContext.Current.CancellationToken);

        var rows = (await _conn.QueryAsync<(string EntryType, long? TtlDays)>(
            "SELECT entry_type, ttl_days FROM entries WHERE path = @path", new { path })).ToList();
        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(row => row.EntryType == "file" && row.TtlDays == null);
    }

    [RetryFact]
    public async Task IngestFileAsync_ReIngestOfAMemoryRow_KeepsTheMemoryKindAndItsTtl()
    {
        var path = await WriteAsync("kept.md", "# Kept\n\noriginal body");
        await _ingestor.IngestFileAsync(_conn, Project, path, null, TestContext.Current.CancellationToken);
        await _conn.ExecuteAsync("UPDATE entries SET entry_type = 'memory', ttl_days = 30 WHERE path = @path", new { path });
        await File.WriteAllTextAsync(path, "# Kept\n\nedited body that hashes differently", TestContext.Current.CancellationToken);

        await _ingestor.IngestFileAsync(_conn, Project, path, null, TestContext.Current.CancellationToken);

        var edited = (await _conn.QueryAsync<(string EntryType, long? TtlDays)>(
            "SELECT entry_type, ttl_days FROM entries WHERE path = @path AND value LIKE '%edited body%'", new { path })).ToList();
        edited.ShouldNotBeEmpty("the edit must have produced a new row");
        edited.ShouldAllBe(row => row.EntryType == "memory" && row.TtlDays == 30);
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
