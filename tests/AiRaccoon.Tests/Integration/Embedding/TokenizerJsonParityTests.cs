using System.Text.Json;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The parity gate for the tokenizer-json family (docs: acceptance criterion 2): for each of 7
///     real HF models, every one of 10 fixture strings must tokenize to EXACTLY the ids HF
///     `tokenizers` 0.22.2 produced (with and without special tokens) — the byte-level BPE models
///     (granite, gte-modernbert, jina, Qwen3), the sentencepiece-byte-fallback model
///     (EmbeddingGemma) and the two WordPiece models (bge-small, nomic) alike, all through
///     <see cref="TokenizerJsonEmbeddingTokenizer" />. The model `tokenizer.json` files are too
///     large to ship (2-20 MB); point <see cref="FixturesDirEnvVar" /> at a directory holding
///     <c>&lt;source&gt;/tokenizer.json</c> per fixture entry (the `source` field in
///     tokenizer-golden.json) to run this locally — it skips otherwise.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class TokenizerJsonParityTests
{
    public const string FixturesDirEnvVar = "AIRACCOON_TOKENIZER_FIXTURES_DIR";

    public static IEnumerable<object[]> Models() =>
        Golden().RootElement.EnumerateObject().Select(p => new object[] { p.Name });

    [RetryTheory]
    [MemberData(nameof(Models))]
    public void Model_MatchesHuggingFaceTokenizersExactly_ForEveryFixtureCase(string modelKey)
    {
        var fixturesDir = Environment.GetEnvironmentVariable(FixturesDirEnvVar);
        if (string.IsNullOrWhiteSpace(fixturesDir) || !Directory.Exists(fixturesDir))
        {
            Assert.Skip($"set {FixturesDirEnvVar} to the directory holding <source>/tokenizer.json per "
                        + "tokenizer-golden.json entry to run the real-model parity gate");
        }

        using var golden = Golden();
        var entry = golden.RootElement.GetProperty(modelKey);
        var source = entry.GetProperty("source").GetString()!;
        var tokenizerJsonPath = Path.Combine(fixturesDir, source, "tokenizer.json");
        if (!File.Exists(tokenizerJsonPath))
        {
            Assert.Skip($"'{tokenizerJsonPath}' does not exist — {FixturesDirEnvVar} is set but this model's fixture is missing");
        }

        var tokenizer = TokenizerJsonEmbeddingTokenizer.Create(tokenizerJsonPath);

        var failures = new List<string>();
        foreach (var testCase in entry.GetProperty("cases").EnumerateArray())
        {
            var text = testCase.GetProperty("text").GetString()!;
            var expectedIds = testCase.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var expectedContentIds = testCase.GetProperty("content_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();

            var actualIds = tokenizer.EncodeToIds(text, addSpecialTokens: true).ToArray();
            var actualContentIds = tokenizer.EncodeToIds(text, addSpecialTokens: false).ToArray();

            if (!expectedIds.SequenceEqual(actualIds))
            {
                failures.Add($"'{Truncate(text)}': expected ids [{string.Join(", ", expectedIds)}], got [{string.Join(", ", actualIds)}]");
            }

            if (!expectedContentIds.SequenceEqual(actualContentIds))
            {
                failures.Add($"'{Truncate(text)}': expected content_ids [{string.Join(", ", expectedContentIds)}], got [{string.Join(", ", actualContentIds)}]");
            }
        }

        failures.ShouldBeEmpty($"{modelKey} ({source}):{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static string Truncate(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private static JsonDocument Golden() =>
        JsonDocument.Parse(File.ReadAllText(TestData.RepoFile("tests/AiRaccoon.Tests/TestData/Tokenizers/tokenizer-golden.json")));
}
