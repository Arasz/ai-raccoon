using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     Production DI wiring (docs/work/2026-08-21-code-search-implementation-plan.md §3.4, WP2):
///     the real container registers the real `CodeChunker` (backed by the bundled code-daemon
///     sentencepiece counting tokenizer) — a directory ingest of a code tree must route through the
///     whole `FileIngestor` → `ICodeIngestor` → `ICodeChunker` seam and write real code rows, while
///     the memory corpus is unaffected (always-ingest mode, §3.3: code chunks even with no code
///     engine configured).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ProdDiCodeChunkerTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-prod-di-code");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task IngestDirectoryAsync_ProdDI_CodeTree_WritesRealCodeRows_MemoryUnaffected()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        services.RegisterCoreMemoryServices(options);
        var provider = services.BuildServiceProvider();

        // Proves the DI graph is actually wired, not that the null-object defaults happen to
        // produce the same result: a missing registration would fall back to
        // NullCodeIngestor/NullCodeFileTypeMatcher silently, which this type check catches.
        provider.GetRequiredService<ICodeChunker>().ShouldBeOfType<CodeChunker>();
        provider.GetRequiredService<ICodeTokenizer>().ShouldBeOfType<CodeTokenizer>();
        provider.GetRequiredService<ICodeIngestor>().ShouldBeOfType<CodeIngestor>();
        provider.GetRequiredService<ICodeFileTypeMatcher>().ShouldBeOfType<CodeFileTypeMatcher>();

        var fileIngestor = provider.GetRequiredService<IFileIngestor>();
        var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @value)",
            new { key = IngestScopeKeys.ScopeGlobal, value = IngestScopeKeys.Serialize([_dataRoot]) });

        await File.WriteAllTextAsync(Path.Combine(_dataRoot, "README.md"), "# hello\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_dataRoot, "Program.cs"), "class Program\n{\n}\n",
            TestContext.Current.CancellationToken);

        await fileIngestor.IngestDirectoryAsync(connection, "acme", _dataRoot, null,
            TestContext.Current.CancellationToken);

        var codeCount = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM code_entries");
        var entryCount = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM entries");
        codeCount.ShouldBeGreaterThan(0, "the real CodeChunker must produce at least one chunk for Program.cs");
        entryCount.ShouldBe(1, "memory ingest is unaffected by the code seam");
    }
}
