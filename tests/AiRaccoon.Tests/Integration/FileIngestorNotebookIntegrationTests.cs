using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Xunit;
using xRetry.v3;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public class FileIngestorNotebookIntegrationTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly FileIngestor _ingestor;
    private readonly string _testDir;

    public FileIngestorNotebookIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"airaccoon_nb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        var sourceStore = new SqliteMemorySourceStore(factory);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker()), new NotebookFileTypeHandler(new NotebookChunker(TestData.RealMarkdownChunker()))]);
        _ingestor = new FileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
            NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            NullWatchStore.Instance, NullEmbedDrainPump.Instance, new IndexableFileWalk(), TestData.SharedScope, new EntryTypeClassifier());

        // Configure global scope to include testDir
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

    [RetryFact]
    public async Task IngestFileAsync_IngestsNotebook_WithCellSectionsAndNoOutputs()
    {
        var path = Path.Combine(_testDir, "analysis.ipynb");
        await File.WriteAllTextAsync(path, """
            {"metadata":{"kernelspec":{"language":"python"}},"nbformat":4,"cells":[
              {"cell_type":"markdown","source":["Loads the dataset."]},
              {"cell_type":"code","source":["df = load()"],"outputs":[{"output_type":"stream","text":["OUTPUT-MARKER"]}]}]}
            """, TestContext.Current.CancellationToken);

        var result = await _ingestor.IngestFileAsync(_conn, "test_project", path, null, TestContext.Current.CancellationToken);

        Assert.True(result.RowsInserted > 0);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT section, value FROM entries WHERE source_file = @path;";
        cmd.Parameters.AddWithValue("@path", path);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string Section, string Content)>();
        while (reader.Read())
        {
            rows.Add((reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetString(1)));
        }

        Assert.Contains(rows, r => r.Section.Contains("cell 2 (code)") && r.Content.Contains("df = load()"));
        Assert.Contains(rows, r => r.Section.Contains("cell 1 (markdown)"));
        Assert.DoesNotContain(rows, r => r.Content.Contains("OUTPUT-MARKER"));
    }
}
