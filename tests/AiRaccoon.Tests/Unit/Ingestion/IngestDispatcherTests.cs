using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>
///     Corpus classification (docs/work/2026-08-21-code-search-implementation-plan.md §3.4): memory
///     wins on overlap (unreachable given disjoint registries, kept as the runtime rule for future
///     drift) — a `.md` file inside a code directory still routes to memory.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IngestDispatcherTests
{
    private readonly CodeFileTypeMatcher _codeMatcher = new();
    private readonly FileTypeMatcher _memoryMatcher = new(
        [new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);

    [Fact]
    public void Classify_MarkdownPath_ReturnsMemory()
    {
        IngestDispatcher.Classify(_memoryMatcher, _codeMatcher, "/repo/src/README.md")
            .ShouldBe(CorpusKind.Memory);
    }

    [Fact]
    public void Classify_CodePath_ReturnsCode()
    {
        IngestDispatcher.Classify(_memoryMatcher, _codeMatcher, "/repo/src/Program.cs")
            .ShouldBe(CorpusKind.Code);
    }

    [Fact]
    public void Classify_UnsupportedPath_ReturnsNeither()
    {
        IngestDispatcher.Classify(_memoryMatcher, _codeMatcher, "/repo/logo.png")
            .ShouldBe(CorpusKind.Neither);
    }

    /// <summary>A `.md` file inside a directory whose siblings are code still routes to memory —
    /// the priority rule is per-file, not per-directory.</summary>
    [Fact]
    public void Classify_MarkdownInsideCodeDirectory_StillReturnsMemory()
    {
        IngestDispatcher.Classify(_memoryMatcher, _codeMatcher, "/repo/src/notes.md")
            .ShouldBe(CorpusKind.Memory);
    }
}
