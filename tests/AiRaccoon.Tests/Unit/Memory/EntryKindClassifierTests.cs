using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     The one rule that says which kind a row holds: a row that mirrors a file on disk has
///     path = source_file, and every other row is agent-written memory. A transcript is a MEMORY —
///     the agent wrote it — even though it carries a source file and is chunked like an ingested
///     document. A declared kind takes precedence over the shape; the shape is the fallback for a
///     row that carries none.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryKindClassifierTests
{
    [Theory]
    [InlineData("memory", "docs/guide.md", "docs/guide.md", EntryKind.Memory)]
    [InlineData("file", "note.md", null, EntryKind.File)]
    [InlineData(null, "docs/guide.md", "docs/guide.md", EntryKind.File)]
    [InlineData("none", "docs/guide.md", "docs/guide.md", EntryKind.File)]
    [InlineData(null, "note.md", "docs/guide.md", EntryKind.Memory)]
    [InlineData(null, "", "", EntryKind.Memory)]
    public void Resolve_PreservesDeclarationsAndOnlyClassifiesUnclassifiedRows(
        string? storedKind, string? path, string? sourceFile, EntryKind expected)
    {
        IEntryKindClassifier classifier = new EntryKindClassifier();
        classifier.Resolve(storedKind, path, sourceFile).ShouldBe(expected);
    }

    /// <summary>
    ///     A stored value this build does not know is not a declaration: it falls back to the
    ///     shape, so the row stays readable instead of carrying a kind nothing can filter on.
    /// </summary>
    [Fact]
    public void Resolve_AnUnknownStoredValue_FallsBackToTheShape()
    {
        IEntryKindClassifier classifier = new EntryKindClassifier();
        classifier.Resolve("transcript", "docs/guide.md", "docs/guide.md").ShouldBe(EntryKind.File);
    }

    /// <summary>
    ///     The three inputs are nullable, and each nullability is load-bearing: a row with no
    ///     declared kind, a row with no path, and a row with no source file all resolve.
    /// </summary>
    [Theory]
    [InlineData(null, null, null, EntryKind.Memory)]
    [InlineData(null, null, "docs/guide.md", EntryKind.Memory)]
    [InlineData("file", null, null, EntryKind.File)]
    [InlineData("memory", null, "docs/guide.md", EntryKind.Memory)]
    [InlineData("none", null, null, EntryKind.Memory)]
    public void Resolve_AcceptsEveryMissingInput(
        string? storedKind, string? path, string? sourceFile, EntryKind expected)
    {
        IEntryKindClassifier classifier = new EntryKindClassifier();
        classifier.Resolve(storedKind, path, sourceFile).ShouldBe(expected);
    }

    /// <summary>
    ///     A row that mirrors a file on disk has path = source_file: it IS the file's chunk. Every
    ///     other row is agent-written memory — a note that cites a file has a source file and is
    ///     still a memory, because re-ingesting that file would not bring the note back.
    /// </summary>
    [Theory]
    [InlineData("docs/guide.md", "docs/guide.md", EntryKind.File)]
    [InlineData("notes/probe.md", "notes/probe.md", EntryKind.File)]
    [InlineData("9f2c.md", "notes/probe.md", EntryKind.Memory)]
    [InlineData("9f2c.md", "hermes/20260806_215718_fd7f66", EntryKind.Memory)]
    [InlineData("9f2c.md", null, EntryKind.Memory)]
    public void Classify_KeysOnTheRowsShape(string? path, string? sourceFile, EntryKind expected) =>
        new EntryKindClassifier().Classify(path, sourceFile).ShouldBe(expected);

    /// <summary>An empty source file is no locator at all: the row is a memory.</summary>
    [Fact]
    public void Classify_AnEmptySourceFile_IsAMemory() =>
        new EntryKindClassifier().Classify("docs/guide.md", "").ShouldBe(EntryKind.Memory);

    /// <summary>The stored form and the wire form agree in both directions.</summary>
    [Theory]
    [InlineData(EntryKind.Memory, "memory")]
    [InlineData(EntryKind.File, "file")]
    [InlineData(EntryKind.None, "none")]
    public void ToStoredValue_NamesEveryKind(EntryKind kind, string expected) =>
        new EntryKindClassifier().ToStoredValue(kind).ShouldBe(expected);

    [Fact]
    public void FromStoredValue_ReadsBackWhatToStoredValueWrote()
    {
        var classifier = new EntryKindClassifier();
        foreach (var kind in new[] { EntryKind.Memory, EntryKind.File, EntryKind.None })
        {
            classifier.FromStoredValue(classifier.ToStoredValue(kind)).ShouldBe(kind);
        }
    }

    /// <summary>
    ///     An unclassified row is the stored name 'none' — the column is NOT NULL, so the
    ///     unclassified state is a value, not a null.
    /// </summary>
    [Fact]
    public void AnUnknownStoredValue_IsNone()
    {
        var classifier = new EntryKindClassifier();
        classifier.FromStoredValue(null).ShouldBe(EntryKind.None);
        classifier.FromStoredValue("transcript").ShouldBe(EntryKind.None);
        classifier.ToStoredValue(EntryKind.None).ShouldBe("none");
    }

    /// <summary>
    ///     A row holds ONE kind, so a set has no stored form. The stored form refuses a set
    ///     loudly rather than writing a value nothing can read — and the refusal cannot be a
    ///     silent null, because a null in a NOT NULL column fails at the write, far from the
    ///     mistake.
    /// </summary>
    [Fact]
    public void ToStoredValue_ASet_RefusesLoudly()
    {
        var classifier = new EntryKindClassifier();
        Should.Throw<ArgumentOutOfRangeException>(() =>
            classifier.ToStoredValue((EntryKind)(EntryKind.Memory | EntryKind.File)));
    }

    /// <summary>
    ///     The QUERY PARAMETER's types are flags, so a caller can ask for a set of them. The set
    ///     lives here and nowhere else: the stored kind is a separate, single-valued type, so the
    ///     parameter lane cannot hand its filter set to the write path.
    /// </summary>
    [Fact]
    public void TheQueryParameterTypes_AreFlags()
    {
        (EntryType.Memory | EntryType.File).ShouldBe(
            (EntryType)(EntryType.Memory | EntryType.File));
        (EntryType.Memory | EntryType.File).HasFlag(EntryType.Memory).ShouldBeTrue();
        (EntryType.Memory | EntryType.File).HasFlag(EntryType.Code).ShouldBeFalse();
    }

    /// <summary>
    ///     The stored kind carries no flag attribute and no Code member: a code chunk is not a row
    ///     of entries — it lives in the separate code_entries corpus, which the query parameter
    ///     reaches as a corpus, not as a row kind.
    /// </summary>
    [Fact]
    public void TheStoredKind_IsSingleValuedAndHasNoCodeMember()
    {
        typeof(EntryKind).GetCustomAttributes(typeof(FlagsAttribute), false).ShouldBeEmpty();
        Enum.GetNames<EntryKind>().ShouldBe(["None", "Memory", "File"]);
    }
}
