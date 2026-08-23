using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     #470: a manifest already on disk can declare a pooling mode its own graph makes unappliable
///     — every build before this one wrote exactly that for faxenoff/code-daemon-embed-v1. The
///     repair corrects it once, at activation, instead of warning (417) on every load; the sha256
///     pins are not the manifest's own, so rewriting it leaves them untouched.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ManifestPoolingRepairTests : IDisposable
{
    private readonly string _dir = TestData.CreateTempRoot("ai-raccoon-manifest-repair");
    private readonly FakeLogger<ManifestPoolingRepair> _logger = new();

    public void Dispose()
    {
        var manifest = Path.Combine(_dir, EmbeddingManifest.FileName);
        if (File.Exists(manifest))
        {
            File.SetAttributes(manifest, FileAttributes.Normal);
        }

        TestData.DeleteTempRoot(_dir);
    }

    [Fact]
    public void Repair_GraphPoolsItsOwnOutput_RewritesThePoolingBlockAndSaysSo()
    {
        SeedManifest(PoolingMode.Cls);
        var before = Manifest();

        Repair(("last_hidden_state", 2)).Repair(_dir).ShouldBeTrue();

        var after = Manifest();
        after.Pooling.Mode.ShouldBe(PoolingMode.ModelOutput);
        after.Onnx.EmbeddingOutput.ShouldBe("last_hidden_state");
        after.Pooling.OutputNames!.Embedding.ShouldBe("last_hidden_state");
        after.Onnx.TokenEmbeddingsOutput.ShouldBe("last_hidden_state");
        after.Onnx.Files.ShouldBe(before.Onnx.Files, "the sha256 pins are not the manifest's own — never touched");
        after.Tokenizer.Files.ShouldBe(before.Tokenizer.Files);
        after.Tokenizer.Options!.SpecialTokens.ShouldBe(before.Tokenizer.Options!.SpecialTokens);
        new EmbeddingManifestValidator().Validate(after).ShouldBeEmpty();

        var record = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(429);
        record.Level.ShouldBe(LogLevel.Information);
        // The ORIGINAL mode, not the new one — the message's literal 'model-output' text already
        // carries the new mode, and a regression here silently duplicated it into this field too.
        record.StructuredState!.Single(kv => kv.Key == "Pooling").Value.ShouldBe("cls");
        record.StructuredState!.Single(kv => kv.Key == "Output").Value.ShouldBe("last_hidden_state");
    }

    [Fact]
    public void Repair_TokenLevelGraph_LeavesTheManifestAlone()
    {
        SeedManifest(PoolingMode.Cls);
        var before = File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName));

        Repair(("last_hidden_state", 3)).Repair(_dir).ShouldBeFalse();

        File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName)).ShouldBe(before);
        _logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    /// <summary>
    ///     #497: a manifest downloaded before #496 already names a distinct
    ///     <c>onnx.embeddingOutput</c> (the planner always name-selected it, independent of
    ///     pooling mode) — the graph's real rank on THAT output, not just
    ///     <c>tokenEmbeddingsOutput</c>, must repair the mode. Both names are left exactly as they
    ///     already are; only <c>pooling.mode</c> and <c>pooling.outputNames.embedding</c> change.
    ///     Every embed until now read <c>tokenOutput</c> under the wrong mode (a genuine,
    ///     still-token-level tensor) — the correction switches the read to a DIFFERENT tensor, so
    ///     this earns its own EventId pair (431/432): 429's "vectors are the same" is false here.
    /// </summary>
    [Fact]
    public void Repair_DistinctEmbeddingOutputGraphPoolsItself_RepairsThePoolingMode()
    {
        TestData.SeedCodeManifestDirectory(_dir);
        TestData.WriteManifestPooling(_dir, PoolingMode.Cls, "token_embeddings", "sentence_embedding");

        Repair(("token_embeddings", 3), ("sentence_embedding", 2)).Repair(_dir).ShouldBeTrue();

        var after = Manifest();
        after.Pooling.Mode.ShouldBe(PoolingMode.ModelOutput);
        after.Onnx.TokenEmbeddingsOutput.ShouldBe("token_embeddings");
        after.Onnx.EmbeddingOutput.ShouldBe("sentence_embedding");
        after.Pooling.OutputNames!.Embedding.ShouldBe("sentence_embedding");
        after.Pooling.OutputNames!.TokenEmbeddings.ShouldBe("token_embeddings");
        new EmbeddingManifestValidator().Validate(after).ShouldBeEmpty();

        var record = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(431);
        record.Level.ShouldBe(LogLevel.Information);
        // The ORIGINAL mode, and the token-level output that was actually read under it — proves
        // this event's own claim (vectors change) rather than reusing 429's (vectors are the same).
        record.StructuredState!.Single(kv => kv.Key == "Pooling").Value.ShouldBe("cls");
        record.StructuredState!.Single(kv => kv.Key == "Output").Value.ShouldBe("sentence_embedding");
        record.StructuredState!.Single(kv => kv.Key == "TokenOutput").Value.ShouldBe("token_embeddings");
        record.Message.ShouldNotContain("vectors this engine", customMessage: "429's identical-vectors claim is false for this shape");
    }

    /// <summary>Review nit: both repair shapes must derive <c>pooling.outputNames.tokenEmbeddings</c>
    /// by the SAME rule (preserve the existing value) — the #497 branch must not overwrite it with
    /// <c>onnx.tokenEmbeddingsOutput</c> just because that field happens to equal it in most cases.</summary>
    [Fact]
    public void Repair_DistinctEmbeddingOutput_PreservesExistingPoolingOutputNamesTokenEmbeddings()
    {
        TestData.SeedCodeManifestDirectory(_dir);
        TestData.WriteManifestPooling(_dir, PoolingMode.Cls, "token_embeddings", "sentence_embedding");
        var serializer = new EmbeddingManifestSerializer();
        var path = Path.Combine(_dir, EmbeddingManifest.FileName);
        var seeded = serializer.Deserialize(File.ReadAllText(path));
        File.WriteAllText(path, serializer.Serialize(seeded with
        {
            Pooling = seeded.Pooling with { OutputNames = seeded.Pooling.OutputNames! with { TokenEmbeddings = "stale_token_name" } }
        }));

        Repair(("token_embeddings", 3), ("sentence_embedding", 2)).Repair(_dir).ShouldBeTrue();

        Manifest().Pooling.OutputNames!.TokenEmbeddings.ShouldBe("stale_token_name");
    }

    /// <summary>Negative control (#497): a stale/wrong <c>embeddingOutput</c> name whose graph rank
    /// is genuinely token-level (3) must not be repaired — only a real rank-2 output earns the
    /// rewrite.</summary>
    [Fact]
    public void Repair_DistinctEmbeddingOutputStillTokenLevel_LeavesTheManifestAlone()
    {
        TestData.SeedCodeManifestDirectory(_dir);
        TestData.WriteManifestPooling(_dir, PoolingMode.Cls, "token_embeddings", "sentence_embedding");
        var before = File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName));

        Repair(("token_embeddings", 3), ("sentence_embedding", 3)).Repair(_dir).ShouldBeFalse();

        File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName)).ShouldBe(before);
        _logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    /// <summary>#497's write-failure pair (432): unlike 430, event 417 never catches this failure
    /// (the output actually read, <c>token_embeddings</c>, is genuinely rank-3), so the message
    /// must say so rather than reuse 430's "417 will warn on every load" claim.</summary>
    [Fact]
    public void Repair_DistinctEmbeddingOutputNotWritable_WarnsThatNothingElseWillCatchIt()
    {
        TestData.SeedCodeManifestDirectory(_dir);
        TestData.WriteManifestPooling(_dir, PoolingMode.Cls, "token_embeddings", "sentence_embedding");
        var path = Path.Combine(_dir, EmbeddingManifest.FileName);
        var before = File.ReadAllText(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Repair(("token_embeddings", 3), ("sentence_embedding", 2)).Repair(_dir).ShouldBeFalse();

        File.SetAttributes(path, FileAttributes.Normal);
        File.ReadAllText(path).ShouldBe(before);
        var record = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(432);
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldContain("will NOT warn");
    }

    [Fact]
    public void Repair_ManifestAlreadySaysModelOutput_LeavesItAlone()
    {
        SeedManifest(PoolingMode.ModelOutput);
        var before = File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName));

        Repair(("last_hidden_state", 2)).Repair(_dir).ShouldBeFalse();

        File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName)).ShouldBe(before);
        _logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Repair_ManifestNotWritable_WarnsAndLeavesItAlone()
    {
        SeedManifest(PoolingMode.Cls);
        var path = Path.Combine(_dir, EmbeddingManifest.FileName);
        var before = File.ReadAllText(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Repair(("last_hidden_state", 2)).Repair(_dir).ShouldBeFalse();

        File.SetAttributes(path, FileAttributes.Normal);
        File.ReadAllText(path).ShouldBe(before);
        var record = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(430);
        record.Level.ShouldBe(LogLevel.Warning);
    }

    /// <summary>Gate review of #475: nothing else on the activation path opens the graph, so a
    /// graph that will not load must be reported here rather than swallowed.</summary>
    [Fact]
    public void Repair_GraphWillNotLoad_WarnsAndLeavesTheManifestAlone()
    {
        SeedManifest(PoolingMode.Cls);
        var before = File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName));
        var repair = new ManifestPoolingRepair(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator(),
            new UnloadableGraph(), _logger);

        repair.Repair(_dir).ShouldBeFalse();

        File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName)).ShouldBe(before);
        var record = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(430);
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldContain("unsupported opset");
    }

    /// <summary>Leaves no temp file behind either — a stray sibling nothing cleans up is litter
    /// inside the user's installed model directory.</summary>
    [Fact]
    public void Repair_LeavesNoTemporaryFileBehind()
    {
        SeedManifest(PoolingMode.Cls);

        Repair(("last_hidden_state", 2)).Repair(_dir).ShouldBeTrue();

        Directory.GetFiles(_dir).ShouldNotContain(f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Repair_DirectoryWithNoManifest_DoesNothing()
    {
        Directory.CreateDirectory(_dir);

        Repair(("last_hidden_state", 2)).Repair(_dir).ShouldBeFalse();

        _logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    private sealed class UnloadableGraph : IOnnxSmokeTester
    {
        public IReadOnlyDictionary<string, int> Verify(string onnxPath) =>
            throw new OnnxSmokeTestException("the downloaded model failed to load in ONNX Runtime: "
                                             + "the export uses an unsupported opset");
    }

    private ManifestPoolingRepair Repair(params (string Output, int Rank)[] ranks) =>
        new(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator(),
            TestData.GraphWithOutputRanks(ranks), _logger);

    private void SeedManifest(PoolingMode mode)
    {
        TestData.SeedCodeManifestDirectory(_dir);
        if (mode != PoolingMode.ModelOutput)
        {
            TestData.WriteManifestPooling(_dir, mode, "last_hidden_state");
        }
    }

    private EmbeddingManifest Manifest() =>
        new EmbeddingManifestSerializer().Deserialize(File.ReadAllText(Path.Combine(_dir, EmbeddingManifest.FileName)));
}
