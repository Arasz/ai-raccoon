using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Provenance gate for the bundled code-daemon-embed-v1 tokenizer asset (WP3-remainder, OQ3
///     approved; docs/work/2026-08-21-code-search-implementation-plan.md §3.3): downloaded from
///     HF repo faxenoff/code-daemon-embed-v1, file `sentencepiece.bpe.model`, pinned by sha256 —
///     a tampered or drifted asset must fail loudly, mirroring <c>BundledModelTests</c>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CodeTokenizerAssetTests
{
    [Fact]
    public void BundledAsset_MatchesThePinnedSha256()
    {
        var path = CodeTokenizer.ResolveModelPath();

        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            .ShouldBe(CodeTokenizer.ModelSha256);
    }

    [Fact]
    public void CountTokens_UsesTheRealBundledTokenizer_AndIsDeterministic()
    {
        var tokenizer = new CodeTokenizer();
        const string text = "def parse_config(path: str) -> dict:\n    return json.load(open(path))";

        var first = tokenizer.CountTokens(text);
        var second = tokenizer.CountTokens(text);

        first.ShouldBeGreaterThan(0);
        second.ShouldBe(first);
    }
}
