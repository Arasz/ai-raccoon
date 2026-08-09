using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     Acceptance fixtures for the five channel families v3 adds over v2 (docs/adr/0018-promotion-scoring-v2.md
///     v3 section, docs/work/2026-08-08-promotion-scoring-round2.md). Content is invented — this repo is
///     public and the real labeled pool quotes private docs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionScoringV3ChannelTests
{
    private static readonly string[] AllProjects = ["ai-raccoon", "ai-badger"];
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private const double CandidateFloor = 0.4;

    private static ExtractionCandidateRow Row(string path, string? sourceFile, string value) =>
        new("h1", path, value, sourceFile, 0.5, 0, Now.AddDays(-5), null);

    [Fact]
    public void RememberDirectoryStatusJournal_ScoresBelowTheCandidateFloor()
    {
        var row = Row(".remember/2026-08-01-status.md", ".remember/2026-08-01-status.md",
            "Daily status for the search-latency workstream: rebased the feature branch onto main this " +
            "morning, ran the fast test filter locally and it came back clean, then pushed the branch " +
            "and opened the follow-up draft PR before lunch. Left the worktree in a clean state for " +
            "tomorrow's continuation. Nothing is currently blocking progress; the next session should " +
            "pick up the batching change where this one left off and re-check the flamegraph before " +
            "touching the cache layer again, since that part of the plan is still unverified.");

        var (score, reasons) = PromotionScorer.Score(row, "ai-raccoon", AllProjects);

        reasons[0].ShouldBe("remember-log");
        score.ShouldBeLessThan(CandidateFloor);
    }

    [Fact]
    public void ClaudeAutoMemorySessionStatusDump_ScoresBelowTheCandidateFloor()
    {
        var row = Row(
            "/Users/dev/.claude/projects/ai-raccoon/memory/session-2026-08-01-handoff.md",
            "/Users/dev/.claude/projects/ai-raccoon/memory/session-2026-08-01-handoff.md",
            "Session handoff for the retrieval team: the worktree at feat/search-latency is rebased onto " +
            "main and pushed to the remote, the fast test filter is green locally, and the full suite is " +
            "queued on CI but has not reported back yet. The next agent picking this up should continue " +
            "the batching change, check the flamegraph before touching the cache layer again, and confirm " +
            "the draft PR description still matches the current diff before marking anything ready.");

        var (score, reasons) = PromotionScorer.Score(row, "ai-raccoon", AllProjects);

        reasons[0].ShouldBe("auto-memory-session");
        score.ShouldBeLessThan(CandidateFloor);
    }

    [Fact]
    public void NamedClaudeAutoMemoryNote_WithMeasuredGotcha_RanksNearTheTop()
    {
        var row = Row(
            "/Users/dev/.claude/projects/ai-raccoon/memory/nuget-lock-starvation-not-node-reuse.md",
            "/Users/dev/.claude/projects/ai-raccoon/memory/nuget-lock-starvation-not-node-reuse.md",
            "Build hangs are NuGet lock starvation, not node reuse: measured wall time stuck at over " +
            "600s until unrelated dotnet processes from a sibling repo's Aspire host were killed, after " +
            "which the build finished in 42s. Always check other repos' Aspire processes first before " +
            "assuming the build server is broken; this is a recorded gotcha, not a flaky-build coincidence.");

        var (score, reasons) = PromotionScorer.Score(row, "ai-raccoon", AllProjects);

        reasons[0].ShouldBe("auto-memory-note");
        // Round-3 lane-A drops the auto-memory-note prior (2.70 -> 2.06, its labelled mean) and adds
        // the (here mildly negative) substance ramp for a chunk this short; 2.9 still reads as "near
        // the top" against the new prior + evidence ceiling (2.06 + 1.4 = 3.46).
        score.ShouldBeGreaterThanOrEqualTo(2.9);
    }

    [Fact]
    public void ClaudeAutoMemoryIndexRow_ScoresLow()
    {
        var row = Row(
            "/Users/dev/.claude/projects/ai-raccoon/memory/MEMORY.md",
            "/Users/dev/.claude/projects/ai-raccoon/memory/MEMORY.md",
            "- [Python tests are not a CI gate](python-tests-not-a-ci-gate.md) — script tests, ruled out " +
            "of scope by the owner\n- [Build hangs are NuGet lock starvation, not node reuse]" +
            "(nuget-lock-starvation-not-node-reuse.md) — check other repos' aspire processes first\n" +
            "- [Several Claude sessions share this repo](ai-raccoon-concurrent-sessions.md) — main moves " +
            "mid-task; always use a worktree");

        var (score, reasons) = PromotionScorer.Score(row, "ai-raccoon", AllProjects);

        reasons[0].ShouldBe("auto-memory-index");
        score.ShouldBeLessThanOrEqualTo(0.71);
    }

    [Fact]
    public void TurnMirrorWithSubstantialProsePrefix_IsRescued_AndScoredOnTheProse()
    {
        // The prose prefix alone exceeds 300 chars and carries a durable rule plus a real measurement;
        // the tool-call transcript is an accidental append, not the payload.
        var row = Row("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2.md", null,
            "Search latency regression fix: batching duplicate embedding requests within a 50ms window " +
            "cut p95 latency from 380ms to 96ms in the retrieval hot path, measured across three separate " +
            "runs on the same corpus and reproduced independently the next day on a second machine. This " +
            "must stay batched — reverting the change silently regresses p95 back above 300ms, and the " +
            "queue depth counter is the signal to check first if this ever happens again in production." +
            "<invoke name=\"Bash\"><parameter name=\"command\">dotnet test</parameter></invoke>" +
            "<invoke name=\"Read\"><parameter name=\"file_path\">/repo/src/Foo.cs</parameter></invoke>");

        var (score, reasons) = PromotionScorer.Score(row, "ai-raccoon", AllProjects);

        reasons[0].ShouldNotBe("turn-mirror");
        score.ShouldBeGreaterThanOrEqualTo(1.5);
    }
}
