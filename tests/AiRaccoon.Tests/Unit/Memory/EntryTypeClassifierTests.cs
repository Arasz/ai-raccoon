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
    /// <summary>
    ///     A row that mirrors a file on disk has path = source_file: it IS the file's chunk. Every
    ///     other row is agent-written memory — a note that cites a file has a source file and is
    ///     still a memory, because re-ingesting that file would not bring the note back.
    /// </summary>
    [Theory]
    [InlineData("docs/guide.md", "docs/guide.md", EntryType.File)]
    [InlineData("notes/probe.md", "notes/probe.md", EntryType.File)]
    [InlineData("9f2c.md", "notes/probe.md", EntryType.Memory)]
    [InlineData("9f2c.md", "hermes/20260806_215718_fd7f66", EntryType.Memory)]
    [InlineData("9f2c.md", null, EntryType.Memory)]
    public void Classify_KeysOnTheRowsShape(string? path, string? sourceFile, EntryType expected) =>
        EntryTypeClassifier.Classify(path, sourceFile).ShouldBe(expected);

    /// <summary>An empty source file is no locator at all: the row is a memory.</summary>
    [Fact]
    public void Classify_AnEmptySourceFile_IsAMemory() =>
        EntryTypeClassifier.Classify("docs/guide.md", "").ShouldBe(EntryType.Memory);

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
