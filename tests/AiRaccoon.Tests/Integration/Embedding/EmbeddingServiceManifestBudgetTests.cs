using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP3 D6/D7/D9: manifest-local chunk budget (ctx − 2 capped at 510), the fingerprint over the
///     manifest's content INCLUDING per-file sha256s, and the tokenizer resolver. The D6 budget is
///     the ONLY WP3 behavior change and it is confined to manifest models — bundled stays 254,
///     openai stays at today's min(256, 8191) = 256.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingServiceManifestBudgetTests
{
    private static string WriteManifestDir(JsonObject manifest, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-budget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }

        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest.ToJsonString());
        return dir;
    }

    private static string ShaOf(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string ShaOfFile(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // D1: pins must match whatever bytes the test actually writes at "vocab.txt"/"model.onnx" —
    // callers that write real fixture content pass its sha256 instead of the "vocab"/"model" default.
    private static JsonObject Manifest(int contextWindowTokens = 8192, string family = "bert-wordpiece",
        string? vocabSha = null, string? onnxSha = null) => new()
    {
        ["manifestVersion"] = 1,
        ["model"] = "budget-test-model",
        ["source"] = new JsonObject { ["repo"] = "test/budget-test-model", ["revision"] = "main" },
        ["provider"] = "local",
        ["dimensions"] = 384,
        ["contextWindowTokens"] = contextWindowTokens,
        ["normalization"] = "l2",
        ["tokenizer"] = new JsonObject
        {
            ["family"] = family,
            ["files"] = new JsonArray(new JsonObject { ["path"] = "vocab.txt", ["sha256"] = vocabSha ?? ShaOf("vocab") })
        },
        ["onnx"] = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = onnxSha ?? ShaOf("model") }),
            ["inputs"] = new JsonArray("input_ids", "attention_mask", "token_type_ids"),
            ["tokenEmbeddingsOutput"] = "last_hidden_state"
        },
        ["pooling"] = new JsonObject { ["mode"] = "mean" }
    };

    private static EmbeddingService Service() =>
        new(new FakeLogger<EmbeddingService>(), new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System);

    [RetryFact]
    public void ResolveChunkBudgetFor_BundledLocal_Stays254()
    {
        Service().ResolveChunkBudgetFor(new EmbeddingSettings("local", null, null, null))
            .ShouldBe(OnnxEmbeddingGenerator.MaxContentTokens);
    }

    [RetryFact]
    public void ResolveChunkBudgetFor_LegacyOnnxFile_Stays254()
    {
        var onnx = Path.Combine(Path.GetTempPath(), "ai-raccoon-budget-tests", Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllText(onnx, "model");

        Service().ResolveChunkBudgetFor(new EmbeddingSettings("local", onnx, null, null))
            .ShouldBe(OnnxEmbeddingGenerator.MaxContentTokens);
    }

    [RetryFact]
    public void ResolveChunkBudgetFor_ManifestWith8192Window_IsCappedAt510()
    {
        var dir = WriteManifestDir(Manifest(8192), ("vocab.txt", "vocab"), ("model.onnx", "model"));

        Service().ResolveChunkBudgetFor(new EmbeddingSettings("local", dir, null, null))
            .ShouldBe(EmbeddingService.MaxManifestChunkTokens, "ctx − 2 = 8190, capped at the D6 constant 510");
    }

    /// <summary>
    ///     D1 cost regression: Load() now hashes every pinned file (including the ~90 MB ONNX
    ///     weights on a real model). ResolveChunkBudgetFor sits on the write hot path (FileIngestor,
    ///     SqliteMemoryStore's ChunkToBudgetAsync) — hashing on every call, not once per engine,
    ///     is the plan's explicitly forbidden shape (plan D1, "not per tool call").
    /// </summary>
    [RetryFact]
    public void ResolveChunkBudgetFor_CalledRepeatedly_HashesTheManifestFilesOnlyOncePerEngine()
    {
        var vocab = BundledModel.ResolveVocabPath();
        var dir = WriteManifestDir(Manifest(vocabSha: ShaOfFile(vocab)), ("vocab.txt", File.ReadAllText(vocab)), ("model.onnx", "model"));
        var hasher = new CountingFileHasher();
        var service = new EmbeddingService(new FakeLogger<EmbeddingService>(), new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator(), hasher),
            NoOpMeasurementRecorder.Instance, TimeProvider.System);
        var settings = new EmbeddingSettings("local", dir, null, null);

        for (var i = 0; i < 5; i++)
        {
            service.ResolveChunkBudgetFor(settings);
        }

        hasher.CallCount.ShouldBeLessThanOrEqualTo(2,
            "the manifest's 2 pinned files must be hashed once per engine fingerprint, not once per call");
    }

    private sealed class CountingFileHasher : IFileHasher
    {
        private readonly IFileHasher _real = new FileHasher();
        public int CallCount { get; private set; }

        public string Sha256OfFile(string path)
        {
            CallCount++;
            return _real.Sha256OfFile(path);
        }
    }

    [RetryFact]
    public void ResolveChunkBudgetFor_ManifestWith300Window_IsCtxMinusTwo()
    {
        var dir = WriteManifestDir(Manifest(300), ("vocab.txt", "vocab"), ("model.onnx", "model"));

        Service().ResolveChunkBudgetFor(new EmbeddingSettings("local", dir, null, null))
            .ShouldBe(298);
    }

    [RetryFact]
    public void ResolveChunkBudgetFor_OpenAi_KeepsTodaySCap()
    {
        Service().ResolveChunkBudgetFor(new EmbeddingSettings("openai", "text-embedding-3-small", null, null))
            .ShouldBe(256, "today's min(256, 8191) — remote chunk budgets are unchanged by WP3");
    }

    [RetryFact]
    public void TrimQueryToWindow_ManifestModel_TrimsToTheSameCap()
    {
        var vocab = BundledModel.ResolveVocabPath();
        var dir = WriteManifestDir(Manifest(8192, vocabSha: ShaOfFile(vocab)), ("vocab.txt", File.ReadAllText(vocab)), ("model.onnx", "model"));
        var service = Service();
        var settings = new EmbeddingSettings("local", dir, null, null);
        var longQuery = string.Join(' ', Enumerable.Repeat(
            "how does the retrieval pipeline weigh full text against vectors when the corpus is large", 40));

        var trimmed = service.TrimQueryToWindow(settings, longQuery);

        var tokenizer = service.ResolveTokenizer(settings)!;
        tokenizer.CountTokens(trimmed).ShouldBeLessThanOrEqualTo(EmbeddingService.MaxManifestChunkTokens);
        tokenizer.CountTokens(trimmed).ShouldBeGreaterThan(EmbeddingService.MaxManifestChunkTokens - 20,
            "the trim must keep as much of the query as fits");
    }

    [RetryFact]
    public void TrimQueryToWindow_ManifestModel_AtTheCap_IsNotTrimmed()
    {
        var vocab = BundledModel.ResolveVocabPath();
        var dir = WriteManifestDir(Manifest(8192, vocabSha: ShaOfFile(vocab)), ("vocab.txt", File.ReadAllText(vocab)), ("model.onnx", "model"));
        var service = Service();
        var settings = new EmbeddingSettings("local", dir, null, null);
        var tokenizer = service.ResolveTokenizer(settings)!;
        var filler = string.Join(' ', Enumerable.Repeat(
            "how does the retrieval pipeline weigh full text against vectors when the corpus is large", 40));
        var atCap = AiRaccoon.Core.Chunking.TokenBudget.Trim(filler,
            EmbeddingService.MaxManifestChunkTokens, new AiRaccoon.Core.Chunking.TokenCount(text => tokenizer.CountTokens(text)));

        var result = service.TrimQueryToWindow(settings, atCap);

        result.ShouldBe(atCap, "a query exactly at the manifest cap must not be trimmed");
    }

    [RetryFact]
    public void EngineFingerprint_ManifestDirectory_HashesTheManifestContent()
    {
        var dir = WriteManifestDir(Manifest(), ("vocab.txt", "vocab"), ("model.onnx", "model"));
        var manifestPath = Path.Combine(dir, EmbeddingManifest.FileName);
        var expected = "local:" + Path.GetFullPath(dir) + "#" +
                       Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(manifestPath)))
                           .ToLowerInvariant();

        Service().EngineFingerprint("local", dir, null).ShouldBe(expected);
    }

    [RetryFact]
    public void EngineFingerprint_ManifestWithNewFileShas_ChangesTheFingerprint()
    {
        var dir = WriteManifestDir(Manifest(), ("vocab.txt", "vocab"), ("model.onnx", "model"));
        var first = Service().EngineFingerprint("local", dir, null);

        // A re-download with new weights rewrites the manifest with new per-file sha256s (D7).
        var manifest = Manifest();
        manifest["onnx"]!["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = ShaOf("model-v2") });
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest.ToJsonString());

        Service().EngineFingerprint("local", dir, null).ShouldNotBe(first);
    }

    [RetryFact]
    public void EngineFingerprint_LegacyAndBundled_AreUnchanged()
    {
        Service().EngineFingerprint("local", null, null).ShouldBe("local:bundled");
        Service().EngineFingerprint("local", "/models/custom.onnx", null).ShouldBe("local:/models/custom.onnx");
    }

    [RetryFact]
    public async Task ResolveTokenizer_ManifestSentencePiece_ReturnsTheManifestTokenizer()
    {
        var spmPath = await TestData.EnsureSentencePieceFixtureAsync(TestContext.Current.CancellationToken);
        var manifest = Manifest(family: "sentencepiece");
        manifest["tokenizer"]!["files"] = new JsonArray(new JsonObject { ["path"] = "sp.model", ["sha256"] = ShaOfFile(spmPath) });
        manifest["tokenizer"]!["options"] = new JsonObject
        {
            ["addBeginOfSentence"] = true,
            ["addEndOfSentence"] = true,
            ["specialTokens"] = new JsonObject { ["<s>"] = 0, ["<pad>"] = 1, ["</s>"] = 2, ["<unk>"] = 3 }
        };
        var dir = WriteManifestDir(manifest, ("sp.model", ""), ("model.onnx", "model"));
        File.Copy(spmPath, Path.Combine(dir, "sp.model"), overwrite: true);
        var settings = new EmbeddingSettings("local", dir, null, null);

        var tokenizer = Service().ResolveTokenizer(settings);

        tokenizer.ShouldBeOfType<SentencePieceEmbeddingTokenizer>();
        tokenizer!.SpecialTokenReservation.ShouldBe(2);
        tokenizer.CountTokens("The quick brown fox jumps over the lazy dog.").ShouldBeGreaterThan(0);
    }

    [RetryFact]
    public void ResolveTokenizer_OpenAi_IsNull()
    {
        Service().ResolveTokenizer(new EmbeddingSettings("openai", "text-embedding-3-small", null, null))
            .ShouldBeNull();
    }

    [RetryFact]
    public void ResolveTokenizer_Bundled_IsTheWordPieceTokenizer()
    {
        Service().ResolveTokenizer(new EmbeddingSettings("local", null, null, null))
            .ShouldBeOfType<WordPieceEmbeddingTokenizer>();
    }

    [RetryFact]
    public void ResolveTokenizer_IsCachedPerFingerprint_SharedWithTheGenerator()
    {
        var vocab = BundledModel.ResolveVocabPath();
        var dir = WriteManifestDir(Manifest(vocabSha: ShaOfFile(vocab)), ("vocab.txt", File.ReadAllText(vocab)), ("model.onnx", "model"));
        var service = Service();
        var settings = new EmbeddingSettings("local", dir, null, null);

        var first = service.ResolveTokenizer(settings);
        var second = service.ResolveTokenizer(settings);

        ReferenceEquals(first, second).ShouldBeTrue("the resolver must cache one tokenizer per engine fingerprint");
    }

    /// <summary>D2/D3: the drain reconciles vec0 to whatever the remote endpoint returns, so the
    /// declared `embedding.dimensions` is what ResolveDimensions must answer — not the 384 default.</summary>
    [RetryFact]
    public void ResolveDimensions_RemoteWithDeclaredDimensions_ReturnsTheDeclaredValue() =>
        Service().ResolveDimensions(new EmbeddingSettings("openai", "text-embedding-3-large", null, null, 3072))
            .ShouldBe(3072);

    [RetryFact]
    public void ResolveDimensions_RemoteWithoutDeclaredDimensions_KeepsTheLegacyAssumption() =>
        Service().ResolveDimensions(new EmbeddingSettings("openai", "text-embedding-3-small", null, null))
            .ShouldBe(384);

    [RetryFact]
    public void ResolveDimensions_BundledLocal_Is384() =>
        Service().ResolveDimensions(new EmbeddingSettings("local", null, null, null)).ShouldBe(384);
}
