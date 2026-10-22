using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     The one rule that says which entry type a row holds. A transcript is a MEMORY — the agent
///     wrote it — even though it carries a source file and is chunked like an ingested document.
///     That is the case the cheap signature gets wrong, so the classification comes from the
///     source's type.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryTypeClassifierTests
{
    [Theory]
    [InlineData(SourceType.Manual, EntryType.Memory)]
    [InlineData(SourceType.Transcript, EntryType.Memory)]
    [InlineData(SourceType.File, EntryType.File)]
    public void FromSource_MapsEverySourceType(SourceType source, EntryType expected) =>
        EntryTypeClassifier.FromSource(source).ShouldBe(expected);

    /// <summary>The stored form and the wire form agree in both directions.</summary>
    [Theory]
    [InlineData(EntryType.Memory, "memory")]
    [InlineData(EntryType.Code, "code")]
    [InlineData(EntryType.File, "file")]
    public void ToStoredValue_NamesEveryType(EntryType type, string expected) =>
        EntryTypeClassifier.ToStoredValue(type).ShouldBe(expected);

    [Fact]
    public void FromStoredValue_ReadsBackWhatToStoredValueWrote()
    {
        foreach (var type in new[] { EntryType.Memory, EntryType.Code, EntryType.File })
        {
            EntryTypeClassifier.FromStoredValue(EntryTypeClassifier.ToStoredValue(type)).ShouldBe(type);
        }
    }

    /// <summary>An unclassified row is None, not a guess: the filter must not silently widen.</summary>
    [Fact]
    public void AnUnknownStoredValue_IsNone()
    {
        EntryTypeClassifier.FromStoredValue(null).ShouldBe(EntryType.None);
        EntryTypeClassifier.FromStoredValue("transcript").ShouldBe(EntryType.None);
        EntryTypeClassifier.ToStoredValue(EntryType.None).ShouldBeNull();
    }

    /// <summary>The types are flags, so a caller can ask for a set of them.</summary>
    [Fact]
    public void TheTypes_AreFlags()
    {
        (EntryType.Memory | EntryType.File).ShouldBe(
            (EntryType)(EntryType.Memory | EntryType.File));
        (EntryType.Memory | EntryType.File).HasFlag(EntryType.Memory).ShouldBeTrue();
        (EntryType.Memory | EntryType.File).HasFlag(EntryType.Code).ShouldBeFalse();
    }
}
