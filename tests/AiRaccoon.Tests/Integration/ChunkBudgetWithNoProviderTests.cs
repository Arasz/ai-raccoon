using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     WP3 step 2 (docs/adr/0063): `FileIngestor.ChunkSizeForAsync` fell back to the o200k counter
///     and the default budget whenever `embedding.provider` was unset, so ingest-then-configure — a
///     supported order — chunked against a counter that does not match the model that later embeds.
///     Configuring the engine re-embeds the bank but never re-chunks it, so those boundaries are
///     permanent.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkBudgetWithNoProviderTests : IAsyncLifetime
{
    /// <summary>The bundled model's window; the budget must leave room for [CLS] and [SEP].</summary>
    private const int BertWindow = 256;

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-chunk-budget-no-provider");
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            new EmbeddingService(new FakeLogger<EmbeddingService>(), new LocalTokenizer(), new EmbeddingTokenizerFactory(), new ProvisionalManifestDescriptor()));

        // Deliberately NOT configured: this is the ingest-then-configure order the defect lives in.
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     With no provider set, nothing embeds yet — but the boundaries drawn now are the ones the
    ///     engine will be handed later, so they must already fit the engine that will draw them.
    /// </summary>
    [Fact]
    public async Task IngestFile_WithNoConfiguredProvider_StillChunksToTheEngineThatWillEmbed()
    {
        var file = Path.Combine(_dataRoot, "long-note.md");
        await File.WriteAllTextAsync(file, BuildLongNote(), TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"),
            IngestScopeKeys.Serialize([_dataRoot]), TestContext.Current.CancellationToken);

        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var entries = await _store.ListContextAsync("acme", ContextNaming.ProjectContext("acme"),
            TestContext.Current.CancellationToken);
        entries.Count.ShouldBeGreaterThan(1);

        var bert = BertTokenizer.Create(BundledModel.ResolveVocabPath(), new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true,
            SplitOnSpecialTokens = true,
            IndividuallyTokenizeCjk = true,
            RemoveNonSpacingMarks = true
        });

        var oversized = entries
            .Select(entry => (entry.Path, Tokens: bert.EncodeToIds(entry.Value, true, true, true).Count))
            .Where(row => row.Tokens > BertWindow)
            .ToList();

        oversized.ShouldBeEmpty(
            $"{oversized.Count} of {entries.Count} chunks exceed the bundled model's {BertWindow}-token "
            + $"window; worst {(oversized.Count == 0 ? 0 : oversized.Max(row => row.Tokens))} tokens. "
            + "Configuring the engine later re-embeds but never re-chunks, so these boundaries are permanent.");
    }

    /// <summary>Real repo prose: synthetic filler tokenizes with a lower BERT/o200k ratio and does
    /// not reproduce the mismatch (see ChunkBudgetIsEngineAwareTests).</summary>
    private static string BuildLongNote() => File.ReadAllText(TestData.RepoFile("docs/plans/2026-08-14-code-quality-improvement-plan.md"));
}
