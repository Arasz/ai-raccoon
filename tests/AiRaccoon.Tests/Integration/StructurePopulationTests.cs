using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Diagnosis proof for the structure modality (vec_structure): ingesting a markdown file with
///     headings through the real store and bundled engine must populate heading_path,
///     structure_embedding and vec_structure, or the fusion in SearchAsync has no structure signal.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class StructurePopulationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow), new TokenizerChunker(),
            new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Ingest_MarkdownWithHeadings_PopulatesTheStructureVectorTable()
    {
        await _store.ConfigureEmbeddingAsync("local", null, null, TestContext.Current.CancellationToken);

        var file = Path.Combine(_dataRoot, "guide.md");
        await File.WriteAllTextAsync(file,
            """
            # Deployment guide

            ## Prerequisites

            Install the runtime and provision the bank before first use.

            ## Rollback

            Restore the previous bank snapshot and restart the server.
            """,
            TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);

        var ingested = await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        ingested.ShouldBe(1);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await Scalar(connection, "SELECT count(*) FROM vec_entries")).ShouldBeGreaterThan(0,
            "content embedding pipeline must have run for the structure claim to mean anything");
        var structuredEntries = await Scalar(connection,
            "SELECT count(*) FROM entries WHERE structure_embedding IS NOT NULL");
        var structureVectorRows = await Scalar(connection, "SELECT count(*) FROM vec_structure");
        structuredEntries.ShouldBeGreaterThan(0, "ingested markdown chunks under headings must carry a structure embedding");
        structureVectorRows.ShouldBeGreaterThan(0,
            "the structure modality searches vec_structure; empty means fusion has no structure signal");
        structureVectorRows.ShouldBe(structuredEntries,
            "every structure_embedding row must have a matching vec_structure row, or the trigger silently dropped one");

        var totalEntries = await Scalar(connection, "SELECT count(*) FROM entries");
        Console.WriteLine(
            $"[StructurePopulationTests] structure-populated fraction: {structuredEntries}/{totalEntries} ({(double)structuredEntries / totalEntries:P1})");
    }

    private static Task<int> Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql) =>
        connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, cancellationToken: TestContext.Current.CancellationToken));
}
