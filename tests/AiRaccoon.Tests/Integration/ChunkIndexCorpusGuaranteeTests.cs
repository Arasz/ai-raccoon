using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     GH #371, in the spirit of <see cref="ChunkingCorpusGuaranteeTests" />: walks this repo's own
///     docs/**/*.md through the PRODUCTION ingest path (real BERT-backed chunker, <see cref="AiRaccoon.Infrastructure.Ingestion.FileIngestor" />,
///     real bank) rather than calling the chunker directly, and checks what the chunker's own output
///     order alone cannot — that chunk_index, read back from the bank, reproduces exactly the order
///     each file's chunks appear in the source text.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class ChunkIndexCorpusGuaranteeTests : IDisposable
{
    private const string ProjectId = "acme";
    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-index-corpus-guarantee");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task IngestingTheDocsCorpus_PersistsChunkIndexInSourceTextOrder()
    {
        var repoRoot = FindRepoRoot();
        var docsRoot = Path.Combine(repoRoot, "docs");

        var bert = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        var chunker = new MarkdownChunker(text => bert.CountTokens(text));
        // Mirrors FileIngestor.ChunkSizeForAsync for an unconfigured bank (docs/adr/0063: an unset
        // provider resolves to the bundled local engine, whose real tokenizer counts every chunk).
        var maxTokens = Math.Min(256, EmbeddingService.SafeChunkBudgetFor("local", null));
        var overlayTokens = Math.Min(ChunkingDefaults.OverlayTokens, Math.Max(0, maxTokens - 1));

        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), chunker, new FakeTimeProvider(DateTimeOffset.UtcNow),
            TestData.CreateEmbeddingService());
        await store.SetSettingAsync(IngestScopeKeys.ScopeProject(ProjectId), IngestScopeKeys.Serialize([docsRoot]),
            TestContext.Current.CancellationToken);

        await store.IngestDirectoryAsync(ProjectId, docsRoot, null, TestContext.Current.CancellationToken);

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
                "SELECT source_file AS SourceFile, chunk_index AS ChunkIndex, value AS Value FROM entries WHERE source_file IS NOT NULL",
                cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();
        rows.ShouldNotBeEmpty();

        var outOfOrder = new List<string>();
        // Scoped to .md, matching ChunkingCorpusGuaranteeTests — the docs tree also carries .json
        // spec files, which route through a different chunker this test does not verify.
        foreach (var group in rows.GroupBy(r => r.SourceFile)
                     .Where(g => g.Key!.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            var byPersistedOrder = group.OrderBy(r => r.ChunkIndex).Select(r => r.Value).ToList();
            var content = await File.ReadAllTextAsync(group.Key!, TestContext.Current.CancellationToken);
            var expected = chunker.Chunk(content, maxTokens, overlayTokens, text => bert.CountTokens(text));

            if (!byPersistedOrder.SequenceEqual(expected, StringComparer.Ordinal))
            {
                outOfOrder.Add(group.Key!);
            }
        }

        outOfOrder.ShouldBeEmpty(
            $"{outOfOrder.Count}/{rows.Select(r => r.SourceFile).Distinct().Count()} files have a chunk_index sequence " +
            $"that does not match source-text order; first: {(outOfOrder.Count > 0 ? outOfOrder[0] : "")}");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs")) && File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repo root not found");
    }

    private sealed class Row
    {
        public string? SourceFile { get; set; }

        public long ChunkIndex { get; set; }

        public string Value { get; set; } = "";
    }
}
