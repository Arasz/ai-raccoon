using System.Text.RegularExpressions;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit;

/// <summary>
///     The WP4 swap moved every slow/E2E/integration test to <c>[RetryFact]</c>/<c>[RetryTheory]</c>.
///     The reflection gates only pin the two probe classes, so a single reverted attribute would silently
///     drop retry coverage; this scans the derived surface for non-retry attribute forms instead.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RetrySurfaceGateTests
{
    private static readonly Regex NonRetryAttribute = new(@"\[Fact([\(\]])|\[Theory([\(\]])", RegexOptions.Compiled);

    [Fact]
    public void EveryRetrySurfaceFile_UsesRetryAttributes()
    {
        var offenders = SurfaceFiles()
            .Where(file => NonRetryAttribute.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "the retry surface must use [RetryFact]/[RetryTheory], not bare [Fact]/[Theory]: "
            + string.Join(", ", offenders));
    }

    /// <summary>An empty sweep passes for the same reason a broken one does.</summary>
    [Fact]
    public void TheSurface_IsNonEmpty() =>
        SurfaceFiles().Count.ShouldBeGreaterThanOrEqualTo(200,
            "the rule scans this set; if it empties, it stops being able to fail");

    /// <summary>Same derivation as the WP4 swap: E2E/ + Integration/ folders ∪ Slow/Nightly trait files, minus the benchmark carve-out.</summary>
    private static List<string> SurfaceFiles() =>
    [
        .. Directory.EnumerateFiles(TestsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(IsInRetrySurface)
    ];

    private static bool IsInRetrySurface(string file)
    {
        if (file.EndsWith("ParityGateTests.cs", StringComparison.Ordinal)
            || file.EndsWith("RetrySurfaceGateTests.cs", StringComparison.Ordinal))
        {
            // ParityGateTests: Performance=Benchmark carve-out. RetrySurfaceGateTests: the gate
            // must not scan its own source (its predicate text contains the trait literals).
            return false;
        }

        var relative = Path.GetRelativePath(TestsRoot, file);
        if (relative.StartsWith($"E2E{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"Integration{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return true;
        }

        var text = File.ReadAllText(file);
        // Same patterns as the WP4 swap's own grep — the [Trait] form of the Speed trait,
        // which this file's own predicate text (and comments) cannot contain literally.
        return text.Contains("Speed, TestCategories.Slow", StringComparison.Ordinal)
            || text.Contains("Speed, TestCategories.Nightly", StringComparison.Ordinal);
    }

    private static string TestsRoot =>
        Path.GetDirectoryName(Path.GetDirectoryName(
            TestData.RepoFile("tests/AiRaccoon.Tests/Unit/RetrySurfaceGateTests.cs")))!;
}
