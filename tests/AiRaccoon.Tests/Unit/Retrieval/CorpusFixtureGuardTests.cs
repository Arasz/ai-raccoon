using System.Linq;
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

    // Measured 2026-08-22 on arm64: 17 231 872 bytes from 199 tracked doc files / 2049 chunks.
    // The ceiling is ~15% headroom. It is a gate, not a target: a regeneration that suddenly
    // doubles the committed binary means a glob widened, and that is worth a red build in a
    // public repository. The floor catches the opposite — a bank that regenerated near-empty
    // would leave every rank gate below passing vacuously on a handful of rows.
    private const long MeasuredBankBytes = 17_231_872;
    private const long BankCeilingBytes = 19_800_000;
    private const long BankFloorBytes = 12_000_000;

    [Fact]
    public void PublicCorpusBank_IsCommittedAndWithinItsSizeBand()
    {
        var path = Path.Combine(RepoRoot(), "tests", "AiRaccoon.Tests", "Resources", "docs-memory.db");

        File.Exists(path).ShouldBeTrue(
            "the public corpus bank Resources/docs-memory.db must be committed (ADR-0090); " +
            "regenerate it with AIRACCOON_REGENERATE_DOCS_CORPUS=1 on an arm64 host.");

        var bytes = new FileInfo(path).Length;
        bytes.ShouldBeLessThanOrEqualTo(BankCeilingBytes,
            $"docs-memory.db is {bytes:N0} bytes; measured {MeasuredBankBytes:N0} when pinned. " +
            "A jump means the corpus selection widened — check scripts/src/corpus_config.py.");
        bytes.ShouldBeGreaterThanOrEqualTo(BankFloorBytes,
            $"docs-memory.db is only {bytes:N0} bytes; measured {MeasuredBankBytes:N0} when pinned. " +
            "A collapse means the selection lost a glob, and the rank gates now pass on almost nothing.");
    }

    // Same composition rule as ForbiddenBankName above, for the same reason (ai-raccoon#455):
    // RealWorldCorpus.cs, RealWorldQueries.cs and the vendored parity golden carried verbatim
    // prose from the private job-search-ai-assistant repo, and the two Python generator files
    // named its checkout path outright.
    private static readonly string ForbiddenJsaaToken = "js" + "aa";
    private static readonly string ForbiddenPrivateRepoName = "job-search-ai-" + "assistant";
    private static readonly string ForbiddenOwnerSurname = "arasz" + "kiewicz";

    private static readonly IReadOnlyList<string> ForbiddenBenchmarkTokens =
    [
        ForbiddenJsaaToken,
        ForbiddenPrivateRepoName,
        ForbiddenOwnerSurname
    ];

    private static readonly IReadOnlyList<string> BenchmarkArtefactRelativePaths =
    [
        "benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs",
        "benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldQueries.cs",
        "tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json",
        "scripts/src/benchmark_corpus.py",
        "scripts/tests/test_benchmark_corpus.py"
    ];

    [Fact]
    public void BenchmarkCorpusArtefacts_CarryNoPrivateRepoContent()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var relativePath in BenchmarkArtefactRelativePaths)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).ShouldBeTrue($"{relativePath} must exist (ai-raccoon#455 benchmark artefact).");

            var content = File.ReadAllText(path);
            offenders.AddRange(
                from token in ForbiddenBenchmarkTokens
                where content.Contains(token, StringComparison.OrdinalIgnoreCase)
                select $"{relativePath} contains '{token}'");
        }

        offenders.ShouldBeEmpty(
            "no benchmark corpus artefact may carry private job-search-ai-assistant prose, its repo " +
            $"name, or the owner's surname (ai-raccoon#455, see ADR-0090). Offenders: {string.Join("; ", offenders)}");
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
