using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The code chunk budget's source of truth (#422, re-measured 2026-08-22). The budget rule is
///     min(510, ctx − reservation); what was wrong before was the ctx it was fed. The exploration
///     spike recorded a "hard 128-token cap" for code-daemon-embed-v1 and the fixture manifest was
///     hand-written to agree with it, so constant and fixture drifted together and nothing noticed.
///     The graph says otherwise: a 514-row position table, positions starting at padding_idx + 1,
///     512 tokens accepted and 513 a hard Gather failure — pinned by
///     <see cref="CodeModelGraphWindowTests" /> against the real weights.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CodeManifestBudgetGuardTests
{
    /// <summary>The rule is ctx-aware, not a flat cap: a genuinely narrow model still resolves
    /// below 510. Kept as the formula's pin now that code-daemon is no longer the narrow case.</summary>
    [Fact]
    public void ResolveChunkBudgetFor_ANarrow128CtxManifest_Resolves126_NotTheFlat510()
    {
        var dir = WriteManifestDir(128);
        var service = new EmbeddingService(new FakeLogger<EmbeddingService>(), new LocalTokenizer(),
            new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()));

        var budget = service.ResolveChunkBudgetFor(new EmbeddingSettings("local", dir, null, null));

        budget.ShouldBe(126, "min(510, 128 - 2) = 126 — the engine plan's flat-510 text must never win");
        budget.ShouldNotBe(EmbeddingService.MaxManifestChunkTokens,
            "510 would mean the ctx-aware D6 rule was reverted to the flat cap");
    }

    /// <summary>
    ///     N2 (derive-not-restate): <see cref="CodeChunker.DefaultBudget" /> must equal what
    ///     <see cref="EmbeddingService.ResolveChunkBudgetFor" /> derives from the REAL
    ///     code-daemon-embed-v1 fixture manifest — so a future edit to either the constant or the
    ///     formula alone is caught, instead of both drifting in silent agreement.
    /// </summary>
    [Fact]
    public void DefaultBudget_EqualsTheFormulasOutput_ForTheRealCodeDaemonFixtureManifest()
    {
        var dir = SeedFixtureManifestDir();
        var service = new EmbeddingService(new FakeLogger<EmbeddingService>(), new LocalTokenizer(),
            new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()));

        var derived = service.ResolveChunkBudgetFor(new EmbeddingSettings("local", dir, null, null));

        derived.ShouldBe(CodeChunker.DefaultBudget,
            "CodeChunker.DefaultBudget must never drift from the manifest-driven formula it hard-codes");
        derived.ShouldBe(EmbeddingService.MaxManifestChunkTokens,
            "code-daemon-embed-v1's measured 512-token window minus the 2-token reservation is 510 — the "
            + "cap and the ctx-derived budget coincide for this model, so neither can hide a change in the other");
    }

    private static string SeedFixtureManifestDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-code-budget-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Resources", "ManifestFixtures",
            "code-daemon-embed-v1.json");
        File.Copy(fixturePath, Path.Combine(dir, EmbeddingManifest.FileName));
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        return dir;
    }

    private static string WriteManifestDir(int contextWindowTokens)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-code-budget-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var manifest = new JsonObject
        {
            ["manifestVersion"] = 1,
            ["model"] = "code-daemon-embed-v1",
            ["source"] = new JsonObject { ["repo"] = "faxenoff/code-daemon-embed-v1", ["revision"] = "main" },
            ["provider"] = "local",
            ["dimensions"] = 768,
            ["contextWindowTokens"] = contextWindowTokens,
            ["normalization"] = "l2",
            ["tokenizer"] = new JsonObject
            {
                ["family"] = "bert-wordpiece",
                ["files"] = new JsonArray(new JsonObject { ["path"] = "vocab.txt", ["sha256"] = ShaOf("vocab") })
            },
            ["onnx"] = new JsonObject
            {
                ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = ShaOf("model") }),
                ["inputs"] = new JsonArray("input_ids", "attention_mask", "token_type_ids"),
                ["tokenEmbeddingsOutput"] = "last_hidden_state"
            },
            ["pooling"] = new JsonObject { ["mode"] = "mean" }
        };
        File.WriteAllText(Path.Combine(dir, "vocab.txt"), "vocab");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest.ToJsonString());
        return dir;
    }

    private static string ShaOf(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
