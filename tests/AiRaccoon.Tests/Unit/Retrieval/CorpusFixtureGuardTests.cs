using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>
///     Guards the corpus fixture contract set by ADR-0090: the private job-search-ai-assistant
///     bank never returns to the tree, and the public replacement stays inside a pinned size
///     ceiling. A .gitignore line is not a gate — this is.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CorpusFixtureGuardTests
{
    // Composed from fragments rather than written as one literal, on purpose: #414's acceptance
    // criterion is that a grep for the private bank's file name across tests/ returns nothing, and
    // a guard that spelled the string it forbids would be its own last offender — permanently
    // red, or permanently exempted.
    private static readonly string ForbiddenBankName = "jsaa" + "-memory";

    private static string ForbiddenBankRelativePath =>
        $"tests/AiRaccoon.Tests/Resources/{ForbiddenBankName}.db";

    [Fact]
    public void PrivateJsaaBank_IsAbsentFromTheTree()
    {
        var path = Path.Combine(RepoRoot(), ForbiddenBankRelativePath);

        File.Exists(path).ShouldBeFalse(
            $"{ForbiddenBankRelativePath} is a bank built from the private job-search-ai-assistant " +
            "tree (ai-raccoon#414). It must never be committed again — see ADR-0090.");
    }

    [Fact]
    public void NoTestSource_ReferencesThePrivateJsaaBank()
    {
        var testsRoot = Path.Combine(RepoRoot(), "tests");

        var offenders = Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Where(file => File.ReadAllText(file).Contains(ForbiddenBankName, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepoRoot(), file))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "no test source may name the private jsaa bank; the public corpus is Resources/docs-memory.db " +
            $"(ADR-0090). Offenders: {string.Join(", ", offenders)}");
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root (AiRaccoon.slnx).");
    }
}
