using System.Text.Json.Nodes;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     WP3 D9 (ADR-0036 invariant): the repair/backfill family and the ingest path resolve budget
///     AND counter through <see cref="IEmbeddingService" />, so a manifest-model bank re-chunks and
///     backfills with the manifest engine's tokenizer — never the bundled one.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RepairFamilyRoutesThroughTheEngineResolverTests
{
    private static string ShaOf(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string WriteManifestDir(JsonObject manifest, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-repair-routing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }

        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest.ToJsonString());
        return dir;
    }

    private static JsonObject SentencePieceManifest() => new()
    {
        ["manifestVersion"] = 1,
        ["model"] = "repair-routing-model",
        ["source"] = new JsonObject { ["repo"] = "test/repair-routing-model", ["revision"] = "main" },
        ["provider"] = "local",
        ["dimensions"] = 384,
        ["contextWindowTokens"] = 8192,
        ["normalization"] = "l2",
        ["tokenizer"] = new JsonObject
        {
            ["family"] = "sentencepiece",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "sp.model", ["sha256"] = ShaOf("sp") }),
            ["options"] = new JsonObject
            {
                ["addBeginOfSentence"] = true,
                ["addEndOfSentence"] = true,
                ["specialTokens"] = new JsonObject { ["<s>"] = 0, ["<pad>"] = 1, ["</s>"] = 2, ["<unk>"] = 3 }
            }
        },
        ["onnx"] = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = ShaOf("model") }),
            ["inputs"] = new JsonArray("input_ids", "attention_mask"),
            ["tokenEmbeddingsOutput"] = "token_embeddings"
        },
        ["pooling"] = new JsonObject { ["mode"] = "cls" }
    };

    private static async Task<SqliteConnection> OpenBankWithSettingsAsync(string provider, string? model,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        await create.ExecuteNonQueryAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO settings (key, value) VALUES ('embedding.provider', @p), ('embedding.model', @m)";
        insert.Parameters.AddWithValue("@p", provider);
        insert.Parameters.AddWithValue("@m", model ?? "");
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static IFileTypeMatcher Matcher() => new FileTypeMatcher(
        [new MarkdownFileTypeHandler(new MarkdownChunker(new TokenCount(new O200kTokenizer().CountTokens)))]);

    private static EmbeddingService Service() =>
        new(new FakeLogger<EmbeddingService>(), new LocalTokenizer(), new EmbeddingTokenizerFactory(), new ProvisionalManifestDescriptor());

    [Fact]
    public async Task ChunkPositionScanner_BudgetAsync_ManifestModel_ResolvesBudgetAndCounterFromTheManifest()
    {
        var spmPath = await TestData.EnsureSentencePieceFixtureAsync(TestContext.Current.CancellationToken);
        var dir = WriteManifestDir(SentencePieceManifest(), ("sp.model", ""), ("model.onnx", "model"));
        File.Copy(spmPath, Path.Combine(dir, "sp.model"), overwrite: true);
        await using var connection = await OpenBankWithSettingsAsync("local", dir, TestContext.Current.CancellationToken);

        var scanner = new ChunkPositionScanner(Matcher(), Service());
        var budget = await scanner.BudgetAsync(connection, TestContext.Current.CancellationToken);

        budget.MaxTokens.ShouldBe(EmbeddingService.MaxManifestChunkTokens,
            "a manifest 8192-window model must budget at the D6 cap, not the bundled 254");
        const string probe = "The quick brown fox jumps over the lazy dog.";
        var sentencePieceCount = budget.CountTokens(probe);
        var bundledCount = new LocalTokenizer().CountTokens(probe);
        sentencePieceCount.ShouldBeGreaterThan(0);
        sentencePieceCount.ShouldNotBe(bundledCount,
            "the repair counter must be the MANIFEST tokenizer, not the bundled wordpiece one (ADR-0036)");
    }

    [Fact]
    public async Task ChunkBackfill_BudgetAsync_ManifestModel_ResolvesBudgetAndCounterFromTheManifest()
    {
        var spmPath = await TestData.EnsureSentencePieceFixtureAsync(TestContext.Current.CancellationToken);
        var dir = WriteManifestDir(SentencePieceManifest(), ("sp.model", ""), ("model.onnx", "model"));
        File.Copy(spmPath, Path.Combine(dir, "sp.model"), overwrite: true);
        await using var connection = await OpenBankWithSettingsAsync("local", dir, TestContext.Current.CancellationToken);

        var backfill = new ChunkBackfill(TestData.RealMarkdownChunker(), TimeProvider.System, Service());
        var (budget, _, countTokens) = await backfill.BudgetAsync(connection, TestContext.Current.CancellationToken);

        budget.ShouldBe(EmbeddingService.MaxManifestChunkTokens);
        countTokens("The quick brown fox jumps over the lazy dog.")
            .ShouldNotBe(new LocalTokenizer().CountTokens("The quick brown fox jumps over the lazy dog."));
    }

    [Fact]
    public async Task ChunkPositionScanner_BudgetAsync_OpenAi_UsesTheO200kProxyCounter()
    {
        await using var connection = await OpenBankWithSettingsAsync("openai", "text-embedding-3-small",
            TestContext.Current.CancellationToken);

        var scanner = new ChunkPositionScanner(Matcher(), Service());
        var budget = await scanner.BudgetAsync(connection, TestContext.Current.CancellationToken);

        budget.MaxTokens.ShouldBe(256, "openai keeps today's min(256, 8191) cap");
        budget.CountTokens("The quick brown fox jumps over the lazy dog.")
            .ShouldBe(new O200kTokenizer().CountTokens("The quick brown fox jumps over the lazy dog."),
                "openai counts with the same o200k proxy the ingest path's chunker-default counter uses (D9)");
    }
}
