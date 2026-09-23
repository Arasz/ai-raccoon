namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     The code-corpus extension registry: owner-approved languages plus HTML, CSS/SCSS, SQL, Vue, shell, Terraform/HCL and Gherkin,
///     case-insensitive. Disjoint from the memory-owned extensions (.md/.markdown/.txt/.json) by test.
/// </summary>
public static class CodeExtensions
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".fsx", ".py", ".ts", ".tsx", ".js", ".jsx", ".go", ".rs",
        ".java", ".kt", ".kts", ".swift", ".rb", ".php", ".c", ".h", ".cc", ".cpp",
        ".hpp", ".m", ".mm", ".scala", ".lua", ".html", ".htm", ".css", ".scss", ".sql",
        ".mjs", ".cjs", ".vue", ".sh", ".tf", ".hcl", ".feature"
    };
}
