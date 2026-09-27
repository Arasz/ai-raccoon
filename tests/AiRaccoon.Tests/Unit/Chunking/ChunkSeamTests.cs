using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ChunkSeamTests
{
    [Theory]
    [InlineData("Invoice reference vk", "83jq was filed", true)]
    [InlineData("call_handler_", "07();", true)]
    [InlineData("Invoice reference ", "vk83jq was filed", false)]
    [InlineData("Invoice reference", " vk83jq", false)]
    [InlineData("first line\n", "second line", false)]
    [InlineData("alpha.", "beta", false)]
    [InlineData("", "beta", false)]
    public void CutsATerm_IsTrueOnlyWhenBothSidesAreTermCharacters(string before, string after, bool expected) =>
        ChunkSeam.CutsATerm(before, after).ShouldBe(expected);

    [Theory]
    [InlineData("```\ndispatch_vk\n```\n", "```\n83jq();\n```\n", true)]
    [InlineData("~~~js\nfoo ab\n~~~\n", "~~~js\ncd bar\n~~~\n", true)]
    [InlineData("```\ndispatch();\n```\n", "```\nnext();\n```\n", false)]
    [InlineData("```\ndispatch_vk\n```\n", "```\n(83jq);\n```\n", false)]
    [InlineData("plain vk\n", "```\n83jq\n```\n", false)]
    [InlineData("```\ndispatch_vk\n```\n", "plain\nvk83jq", false)]
    [InlineData("```\ndispatch_vk\n```", "```\n83jq\n```\n", false)]
    public void MayCutAFencedTerm_NeedsATermOnBothSidesOfAFenceBreak(string before, string after, bool expected) =>
        ChunkSeam.MayCutAFencedTerm(before, after).ShouldBe(expected);
}
