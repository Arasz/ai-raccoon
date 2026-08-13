using AiRaccoon.Infrastructure.Chunking;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class O200kTokenizerTests
{
    [Fact]
    public void O200kBase_CountsKnownStrings()
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        tokenizer.CountTokens("").ShouldBe(0);
        tokenizer.CountTokens("Hello").ShouldBe(1);
        tokenizer.CountTokens("Hello world").ShouldBe(2);
        tokenizer.CountTokens("Hello, world!").ShouldBe(4);
    }

    [Fact]
    public void O200kBase_CountingIsDeterministic()
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        const string text = "The quick brown fox jumps over the lazy dog.";

        tokenizer.CountTokens(text).ShouldBe(tokenizer.CountTokens(text));
    }

    [Fact]
    public void CountTokens_DelegatesToO200kBase()
    {
        var tokenizer = new O200kTokenizer();

        tokenizer.CountTokens("Hello").ShouldBe(1);
        tokenizer.CountTokens("Hello, world!").ShouldBe(4);
    }
}
