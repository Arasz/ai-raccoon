using AiRaccoon.Infrastructure.Embedding.Manifest;
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

namespace AiRaccoon.Tests.Integration.Memory;

/// <summary>
///     WP3 step 1 (docs/adr/0064): `memory_write` did not chunk at all. The only budget-aware
///     chunking in the codebase lived in `FileIngestor`, so a long body stored as ONE row, marked
///     `embed_state='embedded'`, with only its first ~254 WordPiece tokens in the vector. Measured on
///     the live bank: 555 `memory_write` rows, 320 of them over-window, 114,883 tokens never embedded.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WriteChunksToBudgetTests : IAsyncLifetime
{
    private const string ProjectId = "acme";

    /// <summary>The bundled model's window; a chunk must leave room for [CLS] and [SEP].</summary>
    private const int BertWindow = 256;

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-write-chunking");
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var embeddings = new EmbeddingService(new FakeLogger<EmbeddingService>(), new LocalTokenizer(),
            new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            embeddings);
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, factory, embeddings,
            "local", null, null, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     The acceptance criterion: a 10,000-character body produces multiple rows, none over the
    ///     model's window. Before the fix it produced exactly one row of ~2,900 tokens.
    /// </summary>
    [Fact]
    public async Task Write_LongBody_ProducesMultipleRows_NoneOverTheModelWindow()
    {
        var content = LongBody();
        content.Length.ShouldBeGreaterThan(10_000, "the case under test is a body no single chunk can hold");

        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), TestContext.Current.CancellationToken);

        var rows = await _store.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
            TestContext.Current.CancellationToken);
        var tokens = rows.Select(row => (row.Hash, Count: Bert().EncodeToIds(row.Value, true, true, true).Count)).ToList();

        rows.Count.ShouldBeGreaterThan(1,
            $"a {content.Length}-character body must be split; it stored as {rows.Count} row(s) of "
            + $"{string.Join(", ", tokens.Select(t => t.Count))} tokens");
        tokens.Where(t => t.Count > BertWindow).ShouldBeEmpty(
            $"every stored row must fit the model that embeds it; worst {tokens.Max(t => t.Count)} tokens");
    }

    /// <summary>No text may be lost in the split — the reason the acceptance criteria include a length check.</summary>
    [Fact]
    public async Task Write_LongBody_KeepsEveryCharacter()
    {
        var content = LongBody();

        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), TestContext.Current.CancellationToken);

        var rows = await _store.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
            TestContext.Current.CancellationToken);
        rows.Sum(row => row.Value.Length).ShouldBeGreaterThanOrEqualTo(content.Length,
            "chunk overlap may repeat text, but nothing may go missing");
    }

    /// <summary>
    ///     A short write is the overwhelmingly common case and must be untouched: one row, and the
    ///     returned hash still addresses it.
    /// </summary>
    [Fact]
    public async Task Write_ShortBody_StillStoresExactlyOneRow_AddressableByItsHash()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "The discard purge requires age and an absent entry."),
            TestContext.Current.CancellationToken);

        entry.Stored.ShouldBeTrue();
        var rows = await _store.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
            TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(1);

        var fetched = await _store.GetAsync(ProjectId, entry.Hash, TestContext.Current.CancellationToken);
        fetched.ShouldNotBeNull();
        fetched.Value.ShouldBe("The discard purge requires age and an absent entry.");
    }

    /// <summary>
    ///     The hash the caller is handed back must address the chunk it names. Several rows now share
    ///     one path, and the post-insert lookup was `WHERE path = @path LIMIT 1` with no ORDER BY —
    ///     so which row came back was unspecified (docs/adr/0064).
    /// </summary>
    [Fact]
    public async Task Write_LongBody_ReturnsAHashThatAddressesARealRow()
    {
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongBody()),
            TestContext.Current.CancellationToken);

        var fetched = await _store.GetAsync(ProjectId, entry.Hash, TestContext.Current.CancellationToken);

        fetched.ShouldNotBeNull($"the returned hash {entry.Hash} must address a stored row");
        fetched.Value.ShouldBe(entry.Value, "and the row it addresses must be the one that was returned");
    }

    /// <summary>Writing the same long body twice must not double the rows.</summary>
    [Fact]
    public async Task Write_SameLongBodyTwice_DoesNotDuplicateRows()
    {
        var content = LongBody();

        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), TestContext.Current.CancellationToken);
        var after = await _store.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), TestContext.Current.CancellationToken);

        var again = await _store.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
            TestContext.Current.CancellationToken);
        again.Count.ShouldBe(after.Count);
    }

    private static BertTokenizer Bert() =>
        BertTokenizer.Create(BundledModel.ResolveVocabPath(), new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true,
            SplitOnSpecialTokens = true,
            IndividuallyTokenizeCjk = true,
            RemoveNonSpacingMarks = true
        });

    /// <summary>Real repo prose: synthetic filler tokenizes with a lower BERT/o200k ratio and does not
    /// reproduce the mismatch (see ChunkBudgetIsEngineAwareTests).</summary>
    private static string LongBody() => File.ReadAllText(TestData.RepoFile("docs/plans/2026-08-14-code-quality-improvement-plan.md"));
}
