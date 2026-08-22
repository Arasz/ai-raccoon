using AiRaccoon.Infrastructure.Embedding;
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
        record.Id.Id.ShouldBe(424);
        record.Level.ShouldBe(LogLevel.Information);
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

    /// <summary>A manifest already telling the truth is never rewritten — the fingerprint it feeds
    /// must not move on every activation.</summary>
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
        record.Id.Id.ShouldBe(425);
        record.Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public void Repair_DirectoryWithNoManifest_DoesNothing()
    {
        Directory.CreateDirectory(_dir);

        Repair(("last_hidden_state", 2)).Repair(_dir).ShouldBeFalse();

        _logger.Collector.GetSnapshot().ShouldBeEmpty();
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
