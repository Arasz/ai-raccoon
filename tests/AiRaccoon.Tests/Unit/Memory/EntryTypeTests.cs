using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     The query parameter's flag set: which kinds a query asks for. Seven sets are askable (every
///     non-empty subset of Memory, Code and File), the default is all three, the empty set is not a
///     question, an unknown name is a typo rather than a request, and a LOOKUP asks for exactly one
///     kind — a hash alone does not say which corpus holds it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryTypeTests
{
    /// <summary>Every non-empty subset of the three kinds: seven asks, and every one parses.</summary>
    [Theory]
    [InlineData("memory", EntryType.Memory)]
    [InlineData("code", EntryType.Code)]
    [InlineData("file", EntryType.File)]
    [InlineData("memory,code", EntryType.Memory | EntryType.Code)]
    [InlineData("memory,file", EntryType.Memory | EntryType.File)]
    [InlineData("code,file", EntryType.Code | EntryType.File)]
    [InlineData("memory,code,file", EntryType.Memory | EntryType.Code | EntryType.File)]
    public void Parse_ReadsEveryAskingSet(string value, EntryType expected) =>
        EntryTypeParameter.Parse(value).ShouldBe(expected);

    /// <summary>The names are read the way the rest of the surface reads them: case-insensitively.</summary>
    [Theory]
    [InlineData("MEMORY", EntryType.Memory)]
    [InlineData("Memory,Code", EntryType.Memory | EntryType.Code)]
    [InlineData(" memory , file ", EntryType.Memory | EntryType.File)]
    public void Parse_IgnoresCaseAndSpacing(string value, EntryType expected) =>
        EntryTypeParameter.Parse(value).ShouldBe(expected);

    /// <summary>
    ///     The default is the sum, and the sum is NO KIND FILTER: every row, the unclassified ones
    ///     included, so the default answers exactly what today's default (<c>kind=both</c>) answers.
    /// </summary>
    [Fact]
    public void TheDefault_IsEveryKind() =>
        EntryTypeParameter.Default.ShouldBe(EntryType.Memory | EntryType.Code | EntryType.File);

    /// <summary>
    ///     The empty set is not a question: it would ask for nothing and return nothing, which
    ///     reads as a broken bank rather than an empty answer.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void Parse_RefusesTheEmptySet(string value) =>
        Should.Throw<ArgumentException>(() => EntryTypeParameter.Parse(value));

    /// <summary>An unknown name is a typo, not a request: it is refused, never guessed at.</summary>
    [Theory]
    [InlineData("bogus")]
    [InlineData("memory,bogus")]
    [InlineData("notes")]
    [InlineData("none")]
    public void Parse_RefusesAnUnknownName(string value) =>
        Should.Throw<ArgumentException>(() => EntryTypeParameter.Parse(value));

    /// <summary>
    ///     A set carrying a bit this build does not know is refused as well. A name can only ever
    ///     produce a known bit, so this guards the other door: a set assembled elsewhere (a stored
    ///     flag, a computed mask) that names a kind the server has no corpus for.
    /// </summary>
    [Fact]
    public void ASetWithAnUnknownBit_IsRefused() =>
        Should.Throw<ArgumentException>(() =>
            EntryTypeParameter.RefuseUnknownBits(EntryType.Memory | (EntryType)16));

    /// <summary>
    ///     A lookup asks for exactly one kind: a hash alone does not say which corpus holds it, so
    ///     a composite lookup is refused rather than resolved to an arbitrary corpus.
    /// </summary>
    [Fact]
    public void Single_RefusesACompositeLookup()
    {
        Should.Throw<ArgumentException>(() =>
            EntryTypeParameter.Single(EntryType.Memory | EntryType.Code));
        Should.Throw<ArgumentException>(() =>
            EntryTypeParameter.Single(EntryType.Memory | EntryType.Code | EntryType.File));
    }

    /// <summary>
    ///     A lookup names one kind, and that kind has to be one this build knows: a lookup naming a
    ///     kind the server has no corpus for is refused, not resolved to an arbitrary corpus.
    /// </summary>
    [Fact]
    public void Single_RefusesAnUnknownBit()
    {
        Should.Throw<ArgumentException>(() =>
            EntryTypeParameter.Single((EntryType)16));
        Should.Throw<ArgumentException>(() =>
            EntryTypeParameter.Single(EntryType.Memory | (EntryType)16));
    }

    /// <summary>The empty set names no kind, so a lookup refuses it.</summary>
    [Fact]
    public void Single_RefusesTheEmptySet() =>
        Should.Throw<ArgumentException>(() => EntryTypeParameter.Single(EntryType.None));

    /// <summary>The one kind a lookup names comes back as itself.</summary>
    [Theory]
    [InlineData(EntryType.Memory)]
    [InlineData(EntryType.Code)]
    [InlineData(EntryType.File)]
    public void Single_ReturnsTheOneKind(EntryType type) =>
        EntryTypeParameter.Single(type).ShouldBe(type);

    /// <summary>
    ///     The parameter's vocabulary and the stored kind's vocabulary share the word "memory" and
    ///     must not drift apart: a row of kind memory is what a query asking for memory finds.
    /// </summary>
    [Fact]
    public void TheParameterAndTheStoredKind_SpellTheSameWay()
    {
        EntryTypeParameter.Memory.ShouldBe(EntryKindWireNames.Memory);
        EntryTypeParameter.File.ShouldBe(EntryKindWireNames.File);
    }
}
