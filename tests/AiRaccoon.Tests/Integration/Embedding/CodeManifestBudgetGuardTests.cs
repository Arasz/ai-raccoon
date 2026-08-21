using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     H3 guard (docs/work/2026-08-21-code-search-implementation-plan.md §12.1; contracts
///     `.ai-badger/task-tracking/code-mem-implementation-wave3-contracts.md` "Budget rule"): the
///     engine plan's narrative text pins a flat 510-token cap, but the implemented rule is
///     min(510, ctx − reservation). A 128-ctx manifest — code-daemon-embed-v1's own context
///     window — MUST resolve to budget 126, never 510: this pin guards
///     <see cref="AiRaccoon.Infrastructure.Chunking.CodeChunker" />'s budget source against a
///     future re-derivation from the stale plan text.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CodeManifestBudgetGuardTests
{
    [Fact]
    public void ResolveChunkBudgetFor_128CtxManifest_Resolves126_NotTheFlat510()
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
