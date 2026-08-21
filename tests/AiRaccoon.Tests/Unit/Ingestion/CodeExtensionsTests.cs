using AiRaccoon.Core.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>
///     The code extension registry (docs/work/2026-08-21-code-search-implementation-plan.md §3.4,
///     engineer lane §4.1) must never overlap the memory-owned extensions — a derive-or-delete
///     guard against the two registries drifting into the same file type.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CodeExtensionsTests
{
    // Mirrors the memory-owned handlers' extensions (MarkdownFileTypeHandler, JsonFileTypeHandler)
    // without a project reference to Infrastructure — Core must not depend on it.
    private static readonly string[] MemoryExtensions = [".md", ".markdown", ".txt", ".json"];

    [Fact]
    public void All_HasNoOverlapWithMemoryExtensions()
    {
        var overlap = CodeExtensions.All.Intersect(MemoryExtensions, StringComparer.OrdinalIgnoreCase).ToArray();

        overlap.ShouldBeEmpty();
    }

    [Fact]
    public void All_ContainsTheApprovedV1Languages()
    {
        CodeExtensions.All.ShouldBe(
        [
            ".cs", ".fs", ".fsx", ".py", ".ts", ".tsx", ".js", ".jsx", ".go", ".rs",
            ".java", ".kt", ".kts", ".swift", ".rb", ".php", ".c", ".h", ".cc", ".cpp",
            ".hpp", ".m", ".mm", ".scala", ".lua"
        ], ignoreOrder: true);
    }

    [Fact]
    public void All_UsesCaseInsensitiveComparer()
    {
        CodeExtensions.All.Contains(".CS").ShouldBeTrue();
    }
}
