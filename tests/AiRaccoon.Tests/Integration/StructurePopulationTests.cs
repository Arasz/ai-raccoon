using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

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
    private readonly FakeTimeProvider _clock = new(FixedNow);
    private readonly IEventPump<EmbedDrainRequest> _pump = TestData.NewEmbedDrainPump();
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        var sourceStore = new SqliteMemorySourceStore(_factory);
        var embeddings = TestData.CreateEmbeddingService();
        var markdownChunker = TestData.RealMarkdownChunker();
        var matcher = new FileTypeMatcher(
            [new MarkdownFileTypeHandler(markdownChunker), new JsonFileTypeHandler(TestData.RealJsonChunker(markdownChunker))]);
        var fileIngestor = new FileIngestor(matcher, sourceStore, _clock, embeddings,
            NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            NullWatchStore.Instance, _pump);
        var embedder = new EntryEmbedder(embeddings, Substitute.For<IModelMigrationLease>(), _clock, new VecDimensionReconciler());
        _store = new SqliteMemoryStore(_factory, sourceStore, fileIngestor, embedder, _clock,
            NullLogger<SqliteMemoryStore>.Instance, new NoiseFilteringService([]), new SqliteSettingsStore(_factory), _pump,
            NoOpMeasurementRecorder.Instance);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);

        return ValueTask.CompletedTask;
    }

    [RetryFact]
    public async Task Ingest_MarkdownWithHeadings_PopulatesTheStructureVectorTable()
    {
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, _factory, TestData.CreateEmbeddingService(),
            "local", null, null, TestContext.Current.CancellationToken, _clock);

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
        // Ingest only leaves the row pending and enqueues the signal; drain it explicitly to
        // exercise the real embed pass this test is actually about.
        await TestData.DrainEmbedTopicAsync(_factory, _pump,
            new EntryEmbedder(TestData.CreateEmbeddingService(), Substitute.For<IModelMigrationLease>(), _clock, new VecDimensionReconciler()),
            cancellationToken: TestContext.Current.CancellationToken);
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

    private static Task<int> Scalar(SqliteConnection connection, string sql) =>
        connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, cancellationToken: TestContext.Current.CancellationToken));
}
