using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>One graded query over the table corpus, anchored on an answer span rather than a chunk id.</summary>
internal sealed record TableQuery(string Id, string Query, string ExpectedSource, string AnswerSpan, int RelevanceGrade);

/// <summary>
///     The table-bearing graded query set (scripts/table-corpus-queries.json) and the vendored
///     documents it grades — the corpus ADR-0077 recorded as missing.
/// </summary>
internal static partial class TableCorpusCatalog
{
    public const string QueriesRelativePath = "scripts/table-corpus-queries.json";
    public const string CorpusRelativePath = "tests/AiRaccoon.Tests/Resources/TableCorpus";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<TableQuery> Load() =>
        JsonSerializer.Deserialize<TableQuery[]>(File.ReadAllText(TestData.RepoFile(QueriesRelativePath)), JsonOptions)
        ?? [];

    /// <summary>The vendored corpus root, located from any of its files so RepoFile's file-walk applies.</summary>
    public static string CorpusRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, CorpusRelativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate {CorpusRelativePath} from the test output directory.");
    }

    public static IReadOnlyList<string> CorpusFiles() =>
        [.. Directory.EnumerateFiles(CorpusRoot(), "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    /// <summary>The lines of <paramref name="text" /> that belong to a markdown table: a separator row,
    /// the header directly above it, and the body rows directly below.</summary>
    public static IReadOnlyList<string> TableLines(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var inTable = new HashSet<int>();
        for (var index = 1; index < lines.Count; index++)
        {
            if (!SeparatorRow().IsMatch(lines[index]) || !lines[index - 1].TrimStart().StartsWith('|'))
            {
                continue;
            }

            inTable.Add(index - 1);
            inTable.Add(index);
            for (var body = index + 1; body < lines.Count && lines[body].TrimStart().StartsWith('|'); body++)
            {
                inTable.Add(body);
            }
        }

        return [.. inTable.Order().Select(index => lines[index])];
    }

    [GeneratedRegex(@"^\s*\|?[\s:-]*\|[\s:|-]*$")]
    private static partial Regex SeparatorRow();
}
