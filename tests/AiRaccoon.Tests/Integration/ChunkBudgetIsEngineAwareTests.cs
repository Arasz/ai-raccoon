using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
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
///     RAG-F3 (docs/adr/0036): the chunk budget is counted in o200k tokens but the bundled model
///     tokenizes BERT WordPiece — a 1:1 token budget silently truncates chunks at embed time. The
///     local-engine budget must be safe against the model's real tokenizer, and truncation must
///     never happen silently.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkBudgetIsEngineAwareTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private FakeLogger<EmbeddingService> _logger = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _logger = new FakeLogger<EmbeddingService>();
        var embeddings = new EmbeddingService(_logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System);
        var clock = new FakeTimeProvider(FixedNow);
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), clock,
            embeddings, null, null, null, null, null, null, null);
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, factory, embeddings,
            "local", null, null, TestContext.Current.CancellationToken, clock);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task IngestFile_WithLocalEngine_NoChunkExceedsTheBertWordPieceWindow_AndNoTruncationIsLogged()
    {
        var file = Path.Combine(_dataRoot, "long-note.md");
        await File.WriteAllTextAsync(file, BuildLongNote(), TestContext.Current.CancellationToken);
        await AllowIngestScopeAsync(_dataRoot);

        var indexed = await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        indexed.ShouldBe(1);
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
        entries.ShouldAllBe(entry => bert.EncodeToIds(entry.Value, true, true, true).Count <= 256);

        _logger.Collector.GetSnapshot().ShouldNotContain(record => record.Id.Id == 414);
    }

    /// <summary>
    ///     WP2-T11: with the real code-daemon-embed-v1 sentencepiece tokenizer (special ids
    ///     2/3/0/1), a representative real C# source file chunks entirely within the 126-token
    ///     budget — the graph does not truncate, so the caller (the chunker) must never emit an
    ///     over-cap chunk (plan §3.4/§12.1 H3).
    /// </summary>
    [Fact]
    public void CodeChunker_WithTheRealSentencePieceTokenizer_RespectsTheHardCap()
    {
        var tokenizer = new CodeTokenizer();
        var chunker = new CodeChunker(tokenizer);
        var source = File.ReadAllText(TestData.RepoFile("src/AiRaccoon.Core/Chunking/MarkdownChunker.cs"));

        var chunks = chunker.Chunk(source);

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => tokenizer.CountTokens(c.Text) <= CodeChunker.DefaultBudget,
            "no chunk may exceed the 126-token budget once counted with the real sentencepiece tokenizer");
    }

    /// <summary>
    ///     QA WP2 gate: a pathological single line (minified, no whitespace/braces) hard-splits
    ///     under the real sentencepiece cap — the fake-tokenizer unit test (`CodeChunkerTests.
    ///     Chunk_SingleLineOverflow_HardSplitsTheLine`) pins the algorithm; this pins it against
    ///     the real counting tokenizer, where char-count and token-count are not 1:1.
    /// </summary>
    [Fact]
    public void CodeChunker_WithTheRealSentencePieceTokenizer_HardSplitsAPathologicalLongLine()
    {
        var tokenizer = new CodeTokenizer();
        var chunker = new CodeChunker(tokenizer);
        var pathologicalLine = string.Concat(Enumerable.Range(0, 400).Select(i => $"x{i:D3}")) + "\n";

        var chunks = chunker.Chunk(pathologicalLine);

        chunks.Count.ShouldBeGreaterThan(1, "a 1600+ char single line must hard-split under the 126-token cap");
        chunks.ShouldAllBe(c => tokenizer.CountTokens(c.Text) <= CodeChunker.DefaultBudget);
        string.Concat(chunks.Select(c => c.Text)).ShouldBe(pathologicalLine,
            "concatenating chunk values must reproduce the original line");
    }

    /// <summary>Real repo prose (not synthetic repeated paragraphs): the review measured that
    /// synthetic filler tokenizes with a lower BERT/o200k ratio than real English technical prose,
    /// so a real document is what actually reproduces RAG-F3's truncation.</summary>
    private static string BuildLongNote() => File.ReadAllText(TestData.RepoFile("docs/plans/2026-08-14-code-quality-improvement-plan.md"));

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-chunk-budget-tests");

    /// <summary>Ingest is deny-by-default; this test exercises chunk sizing, not containment.</summary>
    private Task AllowIngestScopeAsync(string path) =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([path]),
            TestContext.Current.CancellationToken);
}
