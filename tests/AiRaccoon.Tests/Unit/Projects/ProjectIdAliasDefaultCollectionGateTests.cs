using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     <see cref="ProjectIdAliasDefaultCollection" /> states its membership rule in prose, and
///     prose is not a gate: the rule was already broken twice in one stack — first by test classes
///     that drove the repair job without resetting the cache (a CI red only Linux ordering
///     exposed), then by classes that never joined the collection at all. A hand-maintained mirror
///     needs something that compares the copies (.ai-badger/invariants/derive-or-delete-the-list.md);
///     this is it.
///     <para>
///         The rule, derived rather than listed: a test file holding a call site that replaces
///         <c>ProjectIdAliasMap.Default</c> — directly, or by running the repair job or the startup
///         warm — must serialize with every other reader of that process-wide cache, and must put
///         the cache back afterwards. Reqnroll bindings cannot join an xUnit collection, so they
///         satisfy the rule with an <c>[AfterScenario]</c> reset instead.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ProjectIdAliasDefaultCollectionGateTests
{
    /// <summary>Call sites that leave <c>ProjectIdAliasMap.Default</c> holding something new.</summary>
    private static readonly string[] MutatingCallSites =
    [
        "ReplaceDefault(",
        "LoadAndCacheAsync(",
        "new ProjectIdsRepairJob(",
        "new ProjectIdAliasCacheHostedService("
    ];

    [Fact]
    // Ledger — collection-membership-drift : --filter FullyQualifiedName~ProjectIdAliasDefaultCollectionGateTests :
    // the repo test tree (dropping the [Collection] from SingleProjectIdCensusTests, or the
    // [AfterScenario] reset from SingleProjectIdSteps, reddens with that file named).
    public void EveryTestThatReplacesTheDefaultAliasMapSerializesAndResetsIt()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(TestRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == Path.GetFileName(GateSource())
                || Path.GetFileName(file) == "ProjectIdAliasDefaultCollection.cs")
            {
                continue;
            }

            var source = File.ReadAllText(file);
            if (!MutatingCallSites.Any(call => source.Contains(call, StringComparison.Ordinal)))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            var binding = source.Contains("[Binding]", StringComparison.Ordinal);
            var serialized = binding
                ? source.Contains("[AfterScenario]", StringComparison.Ordinal)
                : source.Contains("ProjectIdAliasDefaultCollection.Name", StringComparison.Ordinal);
            if (!serialized)
            {
                offenders.Add(binding
                    ? $"{name}: replaces Default with no [AfterScenario] teardown"
                    : $"{name}: replaces Default without [Collection(ProjectIdAliasDefaultCollection.Name)]");
            }

            if (!source.Contains("ResetDefault(", StringComparison.Ordinal))
            {
                offenders.Add($"{name}: replaces Default and never calls ResetDefault");
            }
        }

        offenders.ShouldBeEmpty(
            "a test that replaces the process-wide alias cache must serialize with the other Default " +
            "readers and reset it afterwards, or it leaks into whatever runs beside it — " +
            $"found:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    private static string GateSource() => $"{nameof(ProjectIdAliasDefaultCollectionGateTests)}.cs";

    private static string TestRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate tests/ from the test output directory.");
    }
}
