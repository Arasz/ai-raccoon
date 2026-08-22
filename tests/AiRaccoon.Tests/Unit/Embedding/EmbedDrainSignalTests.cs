using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbedDrainSignalTests
{
    [Theory]
    [InlineData(CorpusKind.Memory, EmbedCorpus.Memory)]
    [InlineData(CorpusKind.Code, EmbedCorpus.Code)]
    public void SignalWritten_EnqueuesTheMatchingCorpus(CorpusKind written, EmbedCorpus expected)
    {
        var pump = TestData.NewEmbedDrainPump();

        pump.SignalWritten(written);

        pump.DrainUpTo(10).ShouldHaveSingleItem().Corpus.ShouldBe(expected);
    }

    [Fact]
    public void SignalWritten_Neither_EnqueuesNothing()
    {
        var pump = TestData.NewEmbedDrainPump();

        pump.SignalWritten(CorpusKind.Neither);

        pump.DrainUpTo(10).ShouldBeEmpty();
    }
}
