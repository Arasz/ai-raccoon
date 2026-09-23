using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>Dispatch from a descriptor's tokenizer family string onto the concrete <see cref="IEmbeddingTokenizer" />.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class EmbeddingTokenizerFactoryTests
{
    private static EngineDescriptor Descriptor(string family, string file, SentencePieceTokenizerOptions? options = null) => new(
        "test-model", null, null, 4, 16, "l2", "mean", EngineDescriptor.DefaultSpecialTokenReservation,
        family, file, options, false, ["input_ids", "attention_mask"], "last_hidden_state", null, "model.onnx", []);

    [Fact]
    public void TokenizerJsonFamily_BuildsATokenizerJsonEmbeddingTokenizer()
    {
        var directory = Path.GetDirectoryName(TestData.RepoFile("tests/AiRaccoon.Tests/TestData/Tokenizers/tiny-bytelevel.json"))!;

        var tokenizer = new EmbeddingTokenizerFactory().Create(Descriptor("tokenizer-json", "tiny-bytelevel.json"), directory);

        tokenizer.ShouldBeOfType<TokenizerJsonEmbeddingTokenizer>();
        tokenizer.EncodeToIds("ab c", addSpecialTokens: false).ShouldBe([6, 7]);
    }

    [Fact]
    public void UnknownFamily_ThrowsNamingTheFamilyAndTheModel()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            new EmbeddingTokenizerFactory().Create(Descriptor("warp-drive", "tokenizer.json"), Path.GetTempPath()));

        ex.Message.ShouldContain("warp-drive");
        ex.Message.ShouldContain("test-model");
    }
}
