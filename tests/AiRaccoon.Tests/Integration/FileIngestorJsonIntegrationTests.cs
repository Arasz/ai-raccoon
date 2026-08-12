using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiRaccoon.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public class FileIngestorJsonIntegrationTests : IDisposable
{
    private readonly string _testDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteConnection _conn;
    private readonly SqliteMemorySourceStore _sourceStore;
    private readonly FileIngestor _ingestor;

    public FileIngestorJsonIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_json_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = _factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        _sourceStore = new SqliteMemorySourceStore(_factory);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(), new JsonFileTypeHandler()]);
        _ingestor = new FileIngestor(matcher, new EntryEmbedder(new EmbeddingService()), TimeProvider.System, _sourceStore);

        // Configure global scope to include testDir
        using var scopeCmd = _conn.CreateCommand();
        scopeCmd.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @scope);";
        scopeCmd.Parameters.AddWithValue("@key", IngestScopeKeys.ScopeGlobal);
        scopeCmd.Parameters.AddWithValue("@scope", IngestScopeKeys.Serialize([_testDir]));
        scopeCmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task IngestFileAsync_IngestsJsonFile_AndCreatesChunksInDb()
    {
        var jsonPath = Path.Combine(_testDir, "config.json");
        var jsonContent = """
        {
          "app": "ai-raccoon",
          "version": "1.7.0",
          "features": {
            "json_chunking": true,
            "extensible_handlers": true
          }
        }
        """;
        await File.WriteAllTextAsync(jsonPath, jsonContent, TestContext.Current.CancellationToken);

        var count = await _ingestor.IngestFileAsync(_conn, "test_project", jsonPath, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, count);

        using var checkCmd = _conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM entries WHERE source_file = @path;";
        checkCmd.Parameters.AddWithValue("@path", jsonPath);
        var entryCount = (long)checkCmd.ExecuteScalar()!;

        Assert.True(entryCount > 0, "Expected entries inserted for JSON file");
    }

    [Fact]
    public async Task IngestDirectoryAsync_IngestsJsonAndMarkdownFiles_AndSkipsUnsupported()
    {
        var jsonPath = Path.Combine(_testDir, "data.json");
        await File.WriteAllTextAsync(jsonPath, "{\"key\": \"value\"}", TestContext.Current.CancellationToken);

        var mdPath = Path.Combine(_testDir, "doc.md");
        await File.WriteAllTextAsync(mdPath, "# Documentation\n\nSample text.", TestContext.Current.CancellationToken);

        var pngPath = Path.Combine(_testDir, "image.png");
        await File.WriteAllBytesAsync(pngPath, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);

        var count = await _ingestor.IngestDirectoryAsync(_conn, "test_project", _testDir, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, count); // json + md = 2 files indexed, png skipped
    }

    public void Dispose()
    {
        _conn.Dispose();
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }
}
