using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     No-overlapping-watches policy (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.2/§5.5): containment via IngestPath.IsWithinScope, longest-literal-path tie-break for
///     real-path-equivalent registrations. The same instance backs both the runtime add path and
///     the v11 ladder migration (WatchOverlapResolverTests + MemorySchemaVersionTests both exercise
///     it — one implementation, two call sites). Symlink tie-break cases live in the integration
///     suite (real filesystem symlinks), not here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchOverlapResolverTests
{
    private readonly IWatchOverlapResolver _resolver = new WatchOverlapResolver();
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "watch-overlap-tests");

    private static string Repo(string name) => Path.Combine(Root, name);

    [Fact]
    public void SelectPruned_DisjointWatches_NoneArePruned()
    {
        var registrations = new[]
        {
            new WatchOverlapCandidate(Repo("repo-a"), 1),
            new WatchOverlapCandidate(Repo("repo-b"), 2)
        };

        _resolver.SelectPruned(registrations).ShouldBeEmpty();
    }

    [Fact]
    public void SelectPruned_NestedWatch_PrunesTheNarrower_CoveredByTheOuter()
    {
        var outer = Repo("repo");
        var inner = Path.Combine(outer, "src");
        var registrations = new[]
        {
            new WatchOverlapCandidate(outer, 1),
            new WatchOverlapCandidate(inner, 2)
        };

        var pruned = _resolver.SelectPruned(registrations);

        pruned.ShouldHaveSingleItem();
        pruned[0].Path.ShouldBe(inner);
        pruned[0].CoveredBy.ShouldBe(outer);
    }

    [Fact]
    public void SelectPruned_SiblingLookalike_RepoVsRepo2_NeitherIsPruned()
    {
        var repo = Repo("repo");
        var repo2 = Repo("repo2");
        var registrations = new[]
        {
            new WatchOverlapCandidate(repo, 1),
            new WatchOverlapCandidate(repo2, 2)
        };

        _resolver.SelectPruned(registrations).ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_CandidateBroaderThanExisting_Accepted_ListsExistingAsPruned()
    {
        var inner = Path.Combine(Repo("repo"), "src");
        var existing = new[] { new WatchOverlapCandidate(inner, 1) };
        var candidate = new WatchOverlapCandidate(Repo("repo"), 2);

        var decision = _resolver.Resolve(existing, candidate);

        decision.Outcome.ShouldBe(WatchOverlapOutcome.Accepted);
        decision.Pruned.ShouldHaveSingleItem();
        decision.Pruned[0].Path.ShouldBe(inner);
        decision.Pruned[0].CoveredBy.ShouldBe(candidate.Path);
    }

    [Fact]
    public void Resolve_CandidateNarrowerThanExisting_Rejected_NamesTheCoveringWatch()
    {
        var outer = Repo("repo");
        var existing = new[] { new WatchOverlapCandidate(outer, 1) };
        var candidate = new WatchOverlapCandidate(Path.Combine(outer, "src"), 2);

        var decision = _resolver.Resolve(existing, candidate);

        decision.Outcome.ShouldBe(WatchOverlapOutcome.Rejected);
        decision.CoveringPath.ShouldBe(outer);
        decision.Pruned.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_DisjointCandidate_Accepted_NothingPruned()
    {
        var existing = new[] { new WatchOverlapCandidate(Repo("repo"), 1) };
        var candidate = new WatchOverlapCandidate(Repo("repo2"), 2);

        var decision = _resolver.Resolve(existing, candidate);

        decision.Outcome.ShouldBe(WatchOverlapOutcome.Accepted);
        decision.Pruned.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_ExactLiteralPathReAdd_IsIdempotent_NeitherPrunedNorRejected()
    {
        var path = Repo("repo");
        var existing = new[] { new WatchOverlapCandidate(path, 1) };
        var candidate = new WatchOverlapCandidate(path, 2);

        var decision = _resolver.Resolve(existing, candidate);

        decision.Outcome.ShouldBe(WatchOverlapOutcome.Idempotent);
        decision.Pruned.ShouldBeEmpty();
    }
}
