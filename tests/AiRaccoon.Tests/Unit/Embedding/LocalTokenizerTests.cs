using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     <see cref="LocalTokenizer" /> replaces per-call-site construction of the bundled BERT
///     WordPiece tokenizer (231 KB vocab.txt, ~30k entries) with one shared, lazily-built instance.
///     These tests use the internal factory seam so they never need to actually parse the real vocab
///     — that correctness is covered by the Integration-tier tests that build the real tokenizer.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LocalTokenizerTests
{
    /// <summary>
    ///     Eager construction would turn a broken bundled install into a container-build-time crash
    ///     instead of a first-use failure (BundledModelInstallReplacedException / the missing-asset
    ///     InvalidOperationException). The factory throwing proves it was never invoked.
    /// </summary>
    [Fact]
    public void Constructing_DoesNotBuildTheUnderlyingTokenizer()
    {
        _ = new LocalTokenizer(() => throw new InvalidOperationException(
            "the factory must not run until the first CountTokens call"));
    }

    [Fact]
    public void IsTokenizerBuilt_BeforeAnyCountTokensCall_IsFalse()
    {
        var tokenizer = new LocalTokenizer(() => throw new InvalidOperationException("must not build yet"));

        tokenizer.IsTokenizerBuilt.ShouldBeFalse();
    }
}
