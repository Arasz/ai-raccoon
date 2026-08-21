using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     <see cref="CodeTokenizer" /> lazily builds the bundled code-daemon-embed-v1 sentencepiece
///     tokenizer on first use, mirroring <see cref="LocalTokenizer" />'s factory seam so these tests
///     never need to parse the real 626 KB tokenizer file — that correctness is covered by the
///     Integration-tier tests that build the real tokenizer.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CodeTokenizerTests
{
    [Fact]
    public void Constructing_DoesNotBuildTheUnderlyingTokenizer()
    {
        _ = new CodeTokenizer(() => throw new InvalidOperationException(
            "the factory must not run until the first CountTokens call"));
    }

    [Fact]
    public void IsTokenizerBuilt_BeforeAnyCountTokensCall_IsFalse()
    {
        var tokenizer = new CodeTokenizer(() => throw new InvalidOperationException("must not build yet"));

        tokenizer.IsTokenizerBuilt.ShouldBeFalse();
    }

    [Fact]
    public void ResolveModelPath_WithNonexistentBaseDirectory_Throws()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Should.Throw<InvalidOperationException>(() => CodeTokenizer.ResolveModelPath(missingDir))
            .Message.ShouldContain(CodeTokenizer.ModelFileName);
    }
}
