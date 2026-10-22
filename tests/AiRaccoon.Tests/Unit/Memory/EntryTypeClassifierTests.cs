using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     The one rule that says which entry type a row holds. A transcript is a MEMORY — the agent
///     wrote it — even though it carries a source file and is chunked like an ingested document.
///     Legacy rows use path/source-file equality; declarations take precedence.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryTypeClassifierTests
{
    [Theory]
    [InlineData("memory", "docs/guide.md", "docs/guide.md", EntryType.Memory)]
    [InlineData("file", "note.md", null, EntryType.File)]
    [InlineData(null, "docs/guide.md", "docs/guide.md", EntryType.File)]
    [InlineData(null, "note.md", "docs/guide.md", EntryType.Memory)]
    [InlineData(null, "", "", EntryType.Memory)]
    public void Resolve_PreservesDeclarationsAndOnlyClassifiesLegacyRows(
        string? storedType, string? path, string? sourceFile, EntryType expected)
    {
        IEntryTypeClassifier classifier = new EntryTypeClassifier();
        classifier.Resolve(storedType, path, sourceFile).ShouldBe(expected);
    }

    /// <summary>
    ///     A stored value this build does not know is not a declaration: it falls back to the
    ///     shape, so the row stays readable instead of carrying a kind nothing can filter on.
    /// </summary>
    [Fact]
    public void Resolve_AnUnknownStoredValue_FallsBackToTheShape()
    {
        IEntryTypeClassifier classifier = new EntryTypeClassifier();
        classifier.Resolve("transcript", "docs/guide.md", "docs/guide.md").ShouldBe(EntryType.File);
    }

    /// <summary>
    ///     The three inputs are nullable, and each nullability is load-bearing: a row with no
    ///     declared kind, a row with no path, and a row with no source file all resolve.
    /// </summary>
    [Theory]
    [InlineData(null, null, null, EntryType.Memory)]
    [InlineData(null, null, "docs/guide.md", EntryType.Memory)]
    [InlineData("file", null, null, EntryType.File)]
    [InlineData("memory", null, "docs/guide.md", EntryType.Memory)]
    public void Resolve_AcceptsEveryMissingInput(
        string? storedType, string? path, string? sourceFile, EntryType expected)
    {
        IEntryTypeClassifier classifier = new EntryTypeClassifier();
        classifier.Resolve(storedType, path, sourceFile).ShouldBe(expected);
    }

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
        new EntryTypeClassifier().Classify(path, sourceFile).ShouldBe(expected);

    /// <summary>An empty source file is no locator at all: the row is a memory.</summary>
    [Fact]
    public void Classify_AnEmptySourceFile_IsAMemory() =>
        new EntryTypeClassifier().Classify("docs/guide.md", "").ShouldBe(EntryType.Memory);

    /// <summary>The stored form and the wire form agree in both directions.</summary>
    [Theory]
    [InlineData(EntryType.Memory, "memory")]
    [InlineData(EntryType.Code, "code")]
    [InlineData(EntryType.File, "file")]
    public void ToStoredValue_NamesEveryType(EntryType type, string expected) =>
        new EntryTypeClassifier().ToStoredValue(type).ShouldBe(expected);

    [Fact]
    public void FromStoredValue_ReadsBackWhatToStoredValueWrote()
    {
        var classifier = new EntryTypeClassifier();
        foreach (var type in new[] { EntryType.Memory, EntryType.Code, EntryType.File })
        {
            classifier.FromStoredValue(classifier.ToStoredValue(type)).ShouldBe(type);
        }
    }

    /// <summary>An unclassified row is None, not a guess: the filter must not silently widen.</summary>
    [Fact]
    public void AnUnknownStoredValue_IsNone()
    {
        var classifier = new EntryTypeClassifier();
        classifier.FromStoredValue(null).ShouldBe(EntryType.None);
        classifier.FromStoredValue("transcript").ShouldBe(EntryType.None);
        classifier.ToStoredValue(EntryType.None).ShouldBeNull();
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
