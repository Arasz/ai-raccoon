namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     The v1 code-corpus extension registry (docs/work/2026-08-21-code-search-implementation-plan.md
///     §3.4, engineer lane §4.1) — owner-approved languages, case-insensitive. Disjoint from the
///     memory-owned extensions (.md/.markdown/.txt/.json) by test.
/// </summary>
public static class CodeExtensions
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".fsx", ".py", ".ts", ".tsx", ".js", ".jsx", ".go", ".rs",
        ".java", ".kt", ".kts", ".swift", ".rb", ".php", ".c", ".h", ".cc", ".cpp",
        ".hpp", ".m", ".mm", ".scala", ".lua"
    };
}
