using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     `memory_ingest_file`'s direct-ingest path (<c>SqliteMemoryStore.ReplaceForDirectIngestAsync</c>)
///     leaves the row `pending` and enqueues the embed topic, like every other write path
///     (docs/work/2026-08-22-post-delta-3-plan.md) — it never embeds inline.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DirectIngestEmbedDeferralTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("direct-ingest-embed-deferral");
    private readonly CountingEmbeddingService _embeddings = new();
    private readonly EntryEmbedder _entryEmbedder;
    private readonly SqliteConnectionFactory _factory;
    private readonly IEventPump<EmbedDrainRequest> _pump = TestData.NewEmbedDrainPump();
    private readonly SqliteMemoryStore _store;

    public DirectIngestEmbedDeferralTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _entryEmbedder = new EntryEmbedder(_embeddings, Substitute.For<IModelMigrationLease>(), TimeProvider.System);
        var sourceStore = new SqliteMemorySourceStore(_factory);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        var fileIngestor = new FileIngestor(matcher, sourceStore, TimeProvider.System, _embeddings,
            NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
            NullWatchStore.Instance, _pump);
        _store = new SqliteMemoryStore(_factory, sourceStore, fileIngestor, _entryEmbedder, TimeProvider.System,
            NullLogger<SqliteMemoryStore>.Instance, new NoiseFilteringService([]), new SqliteSettingsStore(_factory),
            _pump);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task IngestFileAsync_LeavesTheRowPending_ThenEmbeddedAfterOneExplicitDrain()
    {
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(EmbeddingSettingsKeys.Provider, "local", TestContext.Current.CancellationToken);
        var file = Path.Combine(_dataRoot, "note.md");
        await File.WriteAllTextAsync(file, "# Note\nsome content worth embedding", TestContext.Current.CancellationToken);

        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        (await EmbedStateAsync(file)).ShouldBe("pending", "the direct-ingest path must not embed inline");
        _embeddings.Calls.ShouldBeEmpty("nothing has been embedded yet — only enqueued");

        await TestData.DrainEmbedTopicAsync(_factory, _pump, _entryEmbedder,
            cancellationToken: TestContext.Current.CancellationToken);

        (await EmbedStateAsync(file)).ShouldBe("embedded", "one explicit drain of the enqueued signal embeds it");
    }

    private async Task<string?> EmbedStateAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT embed_state FROM entries WHERE source_file = @path", new { path });
    }
}
