using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Owner decision 3 (config D, ADR-0125): the query-trim budget follows the engine's chunkTokens
///     by construction — queries are embedded against stored chunks, so they see the same content
///     budget. A fixture manifest with chunkTokens: N trims to (N−20, N] tokens and quotes N in event
///     426; the bundled engine trims at its manifest's chunkTokens (1022 since config D). The
///     coupling is deliberate and ratified.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class QueryTrimFollowsChunkTokensTests : IDisposable
{
    private const int QueryTrimmedEventId = 426;

    private readonly List<string> _manifestDirs = [];

    /// <summary>Every fixture manifest dir this case set up goes away with it (review F7: each case
    /// used to leave one behind).</summary>
    public void Dispose()
    {
        foreach (var dir in _manifestDirs)
        {
            TestData.DeleteTempRoot(dir);
        }
    }

    [RetryTheory]
    [InlineData(700)]
    [InlineData(400)]
    public void TrimQueryToWindow_FixtureWithExplicitChunkTokens_TrimsToThatBudgetAndQuotesIt(int chunkTokens)
    {
        var logger = new FakeLogger<EmbeddingService>();
        var service = Service(logger);
        var settings = new EmbeddingSettings("local", WriteManifestDir(chunkTokens), null, null);
        var tokenizer = service.ResolveTokenizer(settings)!;

        var trimmed = service.TrimQueryToWindow(settings, LongQuery());

        var tokens = tokenizer.CountTokens(trimmed);
        tokens.ShouldBeLessThanOrEqualTo(chunkTokens,
            "the trim budget is the manifest's own chunkTokens, not MaxContentTokens or the 510 cap");
        tokens.ShouldBeGreaterThan(chunkTokens - 20, "the trim must keep as much of the query as fits");
        LoggedMessage(logger).ShouldContain($"{chunkTokens}-token window");
    }

    [RetryFact]
    public void TrimQueryToWindow_BundledEngine_TrimsAtItsManifests1022_AndQuotesIt()
    {
        var logger = new FakeLogger<EmbeddingService>();
        var service = Service(logger);
        var settings = new EmbeddingSettings("local", null, null, null);
        var tokenizer = service.ResolveTokenizer(settings)!;

        var trimmed = service.TrimQueryToWindow(settings, LongQuery());

        var tokens = tokenizer.CountTokens(trimmed);
        tokens.ShouldBeLessThanOrEqualTo(1022,
            "the bundled manifest's chunkTokens is the query budget since config D (ADR-0125)");
        tokens.ShouldBeGreaterThan(1002, "the trim must keep as much of the query as fits");
        LoggedMessage(logger).ShouldContain("1022-token window");
    }

    private static EmbeddingService Service(FakeLogger<EmbeddingService> logger) =>
        new(logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System, TestData.EmbeddingOptions());

    private static string LoggedMessage(FakeLogger<EmbeddingService> logger) =>
        logger.Collector.GetSnapshot().First(r => r.Id.Id == QueryTrimmedEventId).Message;

    private static string LongQuery() =>
        string.Join(' ', Enumerable.Repeat(
            "how does the retrieval pipeline weigh full text against vectors when the corpus is large", 80));

    private string WriteManifestDir(int chunkTokens)
    {
        var vocab = File.ReadAllText(BundledModel.ResolveVocabPath());
        var dir = TestData.CreateTempRoot("ai-raccoon-trim-tests");
        _manifestDirs.Add(dir);
        File.WriteAllText(Path.Combine(dir, "vocab.txt"), vocab);
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), new JsonObject
        {
            ["manifestVersion"] = 1,
            ["model"] = "trim-test-model",
            ["source"] = new JsonObject { ["repo"] = "test/trim-test-model", ["revision"] = "main" },
            ["provider"] = "local",
            ["dimensions"] = 384,
            ["contextWindowTokens"] = 8192,
            ["normalization"] = "l2",
            ["tokenizer"] = new JsonObject
            {
                ["family"] = "bert-wordpiece",
                ["files"] = new JsonArray(new JsonObject { ["path"] = "vocab.txt", ["sha256"] = ShaOf(vocab) })
            },
            ["onnx"] = new JsonObject
            {
                ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = ShaOf("model") }),
                ["inputs"] = new JsonArray("input_ids", "attention_mask", "token_type_ids"),
                ["tokenEmbeddingsOutput"] = "last_hidden_state"
            },
            ["pooling"] = new JsonObject { ["mode"] = "mean" },
            ["chunkTokens"] = chunkTokens
        }.ToJsonString());
        return dir;
    }

    private static string ShaOf(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
