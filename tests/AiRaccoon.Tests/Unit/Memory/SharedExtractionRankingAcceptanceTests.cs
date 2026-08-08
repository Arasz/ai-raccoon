using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     Headline acceptance test for docs/adr/0018-promotion-scoring-v2.md: synthetic candidates covering
///     the archetypes named in the eval report, with invented content (this repo is public; the real
///     candidate pool quotes private docs). Against the pre-v2 additive scorer this goes red — a plan
///     chunk that merely mentions a sibling project outranks an organic measurement write, because the
///     incumbent's `+2 cross-project` and `+2 organic-write` are both flat bare-substring/null-check
///     bonuses that collapse to the same few values on a multi-project bank (see the eval report's
///     "why the incumbent fails": 61/61 candidates matched cross-project, 61/61 matched recent).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SharedExtractionRankingAcceptanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] AllProjects = ["svc-orders", "svc-billing", "svc-search"];

    private readonly SharedExtractionService _service = new();

    private static ExtractionCandidateRow Row(
        string hash, string path, string? sourceFile, string value, int accessCount = 0) =>
        new(hash, path, value, sourceFile, 0.5, accessCount, Now.AddDays(-5), null);

    /// <summary>The candidate pool: one invented chunk per archetype named in docs/adr/0018-promotion-scoring-v2.md,
    /// plus the two organic edge cases from the second validation round (status dump with test counts,
    /// one-line dated contract fact).</summary>
    private static IReadOnlyList<ExtractionCandidateRow> Candidates() =>
    [
        Row("organic-measurement", "organic-measurement.md", null,
            "Measured wall time dropped from 640ms to 210ms after moving retries off the request " +
            "thread, and peak RSS fell 18%. Reproduced across 40 commits worth of the same benchmark " +
            "harness, so the drop is not evidence of noise from an unrelated deploy elsewhere in the " +
            "stack. Throughput rose 2x under the same load, verified independently on a second machine " +
            "before merging anything, and the queue depth metric stayed flat throughout the exercise."),

        Row("adr", "docs/adr/0021-cache-invalidation.md", "docs/adr/0021-cache-invalidation.md",
            "This decision is final: consumers must never bypass the invalidation queue directly, even " +
            "under load, because the queue is the only component that guarantees an ordered view of " +
            "cache state across every replica. Bypassing it is not evidence of a performance win — it " +
            "is a correctness bug waiting to happen, and the invariant is required reading before " +
            "touching this subsystem, recorded here so nobody has to relearn the reasoning the hard way."),

        Row("explanation", "docs/explanation/queue-architecture.md", "docs/explanation/queue-architecture.md",
            "# Queue architecture\nThe retry queue exists to decouple request handling from delivery: " +
            "producers enqueue a message and return immediately, while a pool of workers drains the " +
            "queue at a pace the downstream system can sustain. This separation is what lets the " +
            "service absorb bursts without shedding load, and it is why every write path in the " +
            "service goes through the queue rather than calling downstream services directly."),

        Row("measurement-sweep", "docs/work/cache-benchmark-results.md", "docs/work/cache-benchmark-results.md",
            "Benchmarked five eviction policies against the same replay trace. LRU held a 92% hit rate " +
            "at steady state, ARC reached 94% but cost 30% more CPU, and a simple TTL-only policy " +
            "trailed at 81%. The sweep ran for 3 hours across 12 configurations, and the numbers were " +
            "stable within 2% across three repeated runs of the same harness on the same machine."),

        Row("dated-work-note", "docs/work/2026-05-02-cache-notes.md", "docs/work/2026-05-02-cache-notes.md",
            "Spent the afternoon reading through the eviction code before making any changes. The LRU " +
            "implementation touches a shared lock on every access, which is probably fine at current " +
            "load but worth watching if traffic triples. Nothing actionable yet, just orientation notes " +
            "for whoever picks this up next after the current sprint wraps up next week sometime."),

        Row("changelog", "docs/changelog/0.104.0-widget-release-notes.md",
            "docs/changelog/0.104.0-widget-release-notes.md",
            "0.104.0 ships the new eviction policy behind a feature flag, fixes a race in the retry " +
            "queue's shutdown path, and removes the deprecated v1 cache warm-up script that nobody has " +
            "called in over a year according to the access logs the team checked before removing it."),

        // The defect fixture: a plan chunk that merely mentions a sibling project (svc-billing) near its
        // opening — under the old scorer this out-scores the organic measurement.
        Row("plan-mentions-foreign-project", "docs/plans/2026-05-01-cache-plan.md",
            "docs/plans/2026-05-01-cache-plan.md",
            "Perf review needed before merge, with sign-off from svc-billing's on-call since the two " +
            "services share the eviction library and any regression there would affect both of them at " +
            "once. Work happens on a worktree ahead of Friday's freeze, and the rollout otherwise " +
            "follows the standard checklist without any special casing needed for this particular " +
            "release, so no further coordination beyond the usual sign-off should be necessary at all.",
            accessCount: 3),

        Row("turn-mirror", "session.md", null,
            "<invoke name=\"Bash\"><parameter name=\"command\">dotnet test</parameter></invoke>" +
            "<invoke name=\"Read\"><parameter name=\"file_path\">/repo/src/Foo.cs</parameter></invoke> " +
            "Ran the suite after the change and everything came back green across the board with " +
            "nothing left to follow up on right now, all clear for the next step in the pipeline."),

        Row("doc-index", "docs/work/README.md", "docs/work/README.md",
            "| Date | Topic |\n| --- | --- |\n| [2026-05-01](2026-05-01-cache-plan.md) | Cache plan |\n" +
            "| [2026-05-02](2026-05-02-cache-notes.md) | Cache notes |\n" +
            "| [2026-04-01](2026-04-01-perf-review.md) | Perf review |\n" +
            "| [2026-03-01](2026-03-01-research-synthesis.md) | Research synthesis |"),

        Row("superseded-review", "docs/work/reviews/2026-04-01-perf-review.md",
            "docs/work/reviews/2026-04-01-perf-review.md",
            "This review's recommendation was superseded by the May cache sweep; the historical note " +
            "below no longer applies to the current eviction policy and is kept only so the reasoning " +
            "trail stays intact for anyone auditing why the old policy was dropped in the first place."),

        Row("reference", "docs/reference/cli-flags.md", "docs/reference/cli-flags.md",
            "--cache-size sets the maximum number of entries held in memory before eviction begins; " +
            "--cache-ttl sets how long an entry survives without being touched; --cache-policy selects " +
            "lru, arc, or ttl-only. All three flags accept an environment-variable override with the " +
            "same name, upper-cased and prefixed with SVC_ so scripts can discover them at runtime."),

        Row("catalog-page", "docs/skills.md", "docs/skills.md",
            "This project's skills catalog lists every reusable playbook available to agents working " +
            "in this repo, organized by the stage of work each one supports, from planning through " +
            "review, with a one-line description of when to reach for each entry in the list below."),

        Row("charter", "docs/work/2026-05-01-review-charter.md", "docs/work/2026-05-01-review-charter.md",
            "Standing rule for every review in this cycle: a cited log line is not evidence of a fix; " +
            "only a rerun that reproduces the failure and then shows it gone counts. This is by design, " +
            "not a formality, and reviewers should reject any write-up that skips the rerun entirely."),

        Row("organic-status-dump", "status.md", null,
            "Done. Everything shipped to main, the full suite passed: 3272 passed, 0 failed, 0 " +
            "skipped, exit 0 in 3m18s. CI checks are green across the whole pipeline and the branch " +
            "was merged and pushed to origin/main with nothing left in flight for this rollout at all."),

        Row("organic-dated-fact", "fact.md", null,
            "(2026-05-01): the retry queue must never exceed one in-flight message per partition, by design.")
    ];

    [Fact]
    public void OrganicMeasurementAndAdr_OutrankEveryPlanChangelogAndIndexChunk()
    {
        var result = _service.Run(ExtractMode.Propose, "svc-orders", AllProjects, Candidates(), [], [], false,
            20, Now);
        var byHash = result.Candidates.ToDictionary(c => c.Hash);

        byHash.ShouldContainKey("organic-measurement");
        byHash.ShouldContainKey("adr");
        byHash.ShouldContainKey("plan-mentions-foreign-project");
        byHash.ShouldContainKey("changelog");

        var floor = new[] { "plan-mentions-foreign-project", "changelog" }.Select(h => byHash[h].Score).Max();
        byHash["organic-measurement"].Score.ShouldBeGreaterThan(floor);
        byHash["adr"].Score.ShouldBeGreaterThan(floor);
    }

    [Fact]
    public void DocIndexAndTurnMirror_ScoreBelowTheFloor_AndAreExcluded()
    {
        var result = _service.Run(ExtractMode.Propose, "svc-orders", AllProjects, Candidates(), [], [], false,
            20, Now);
        var hashes = result.Candidates.Select(c => c.Hash).ToHashSet();

        hashes.ShouldNotContain("doc-index");
        hashes.ShouldNotContain("turn-mirror");
    }

    /// <summary>Second validation round (docs/adr/0018-promotion-scoring-v2.md): the un-refined
    /// archetype+evidence model alone saturates the organic prior and cannot tell a status/turn-mirror
    /// dump from a durable fact — test-result counts ("3272 passed... exit 0") read as measurements.</summary>
    [Fact]
    public void OrganicStatusDumpWithTestCounts_ScoresBelowTheFloor()
    {
        var result = _service.Run(ExtractMode.Propose, "svc-orders", AllProjects, Candidates(), [], [], false,
            20, Now);

        result.Candidates.ShouldNotContain(c => c.Hash == "organic-status-dump");
    }

    [Fact]
    public void OrganicOneLineDatedContractFact_ScoresWell()
    {
        var result = _service.Run(ExtractMode.Propose, "svc-orders", AllProjects, Candidates(), [], [], false,
            20, Now);
        var candidate = result.Candidates.Single(c => c.Hash == "organic-dated-fact");

        candidate.Score.ShouldBeGreaterThanOrEqualTo(2.2);
    }

    /// <summary>The defect made visible: the old additive scorer's `+2 cross-project` (bare substring)
    /// and `+1 accessed` push a plan chunk that merely mentions a sibling project above an organic
    /// measurement write. Run this test against the pre-v2 SharedExtractionService to see it fail.</summary>
    [Fact]
    public void PlanChunkMentioningAForeignProject_DoesNotOutrankTheOrganicMeasurement()
    {
        var result = _service.Run(ExtractMode.Propose, "svc-orders", AllProjects, Candidates(), [], [], false,
            20, Now);
        var byHash = result.Candidates.ToDictionary(c => c.Hash);

        byHash["organic-measurement"].Score.ShouldBeGreaterThan(byHash["plan-mentions-foreign-project"].Score);
    }
}
