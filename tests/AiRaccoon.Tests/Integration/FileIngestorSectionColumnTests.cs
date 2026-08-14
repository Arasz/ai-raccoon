using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     SourcePathQuery resolves a "file#section" query by ANDing the anchor against the FTS
///     columns {source_file section}. FileIngestor wrote section as null for every chunk, so that
///     documented behaviour could not work for any ingested document — the bulk of every bank.
///     Measured on the live bank before the fix: section populated on 517 of 15,325 rows (all from
///     memory_write's explicit section argument) against heading_path on 5,604.
///     The old retrieval fixture hid this because it was built by scripts/src/chunking.py, a
///     pipeline that does populate section and that the product does not ship.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public class FileIngestorSectionColumnTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly FileIngestor _ingestor;
    private readonly string _testDir;

    public FileIngestorSectionColumnTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_section_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        _ingestor = new FileIngestor(matcher, new EntryEmbedder(TestData.CreateEmbeddingService()),
            new SqliteMemorySourceStore(factory), TimeProvider.System);

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
    public async Task IngestFileAsync_PopulatesSectionFromTheHeadingLeaf()
    {
        var file = Path.Combine(_testDir, "adr.md");
        await File.WriteAllTextAsync(file,
            "# ADR-0011 Frontend chassis\n\n## Context\n\nThe app needs a shell.\n\n## Decision\n\nWe adopt the chassis pattern.\n",
            TestContext.Current.CancellationToken);

        await _ingestor.IngestFileAsync(_conn, "acme", file, null, TestContext.Current.CancellationToken);

        var sections = new List<string?>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT section FROM entries WHERE source_file = @p ORDER BY chunk_index";
        cmd.Parameters.AddWithValue("@p", file);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            sections.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        sections.ShouldNotBeEmpty();
        sections.Where(s => s != null).ShouldContain(s => s!.Contains("Decision", StringComparison.OrdinalIgnoreCase),
            "the chunk under '## Decision' must carry that heading as its section, or a '#decision' " +
            "path query has nothing to match against");
    }
}
