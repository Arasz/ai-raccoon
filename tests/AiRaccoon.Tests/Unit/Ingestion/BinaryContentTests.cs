using AiRaccoon.Core.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>A NUL in the first 8,000 characters marks content as binary — git's own heuristic.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BinaryContentTests
{
    [Fact]
    public void Text_IsNotBinary() => BinaryContent.IsBinary("class Program\n{\n}\n").ShouldBeFalse();

    [Fact]
    public void Empty_IsNotBinary() => BinaryContent.IsBinary(string.Empty).ShouldBeFalse();

    [Fact]
    public void NulNearTheStart_IsBinary() => BinaryContent.IsBinary("MZ\u0090\0\u0003\0").ShouldBeTrue();

    [Fact]
    public void NulAtTheLastInspectedCharacter_IsBinary() =>
        BinaryContent.IsBinary(new string('a', BinaryContent.InspectedLength - 1) + "\0").ShouldBeTrue();

    [Fact]
    public void NulPastTheInspectedPrefix_IsNotBinary() =>
        BinaryContent.IsBinary(new string('a', BinaryContent.InspectedLength) + "\0").ShouldBeFalse();
}
