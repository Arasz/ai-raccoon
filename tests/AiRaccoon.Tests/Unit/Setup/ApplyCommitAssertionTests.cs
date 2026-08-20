using AiRaccoon.Tests.Integration.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Truth table for <see cref="ApplyCommitAssertions.IsHonestApplyCommit" /> — the composite
///     that replaced exact equality on the changed set because the maintenance loop's 15 s
///     on-demand poll may consume the outbox request inside the command's window on a loaded
///     machine (2026-08-20 nightly F5). The table pins the gate's honest claim: the request row
///     must exist, nothing outside {outbox, maintenance_jobs, relay targets} may change, and a
///     domain mutation must carry a maintenance stamp.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ApplyCommitAssertionTests
{
    private const string Outbox = "promotion_queue_prune_requests";

    private static readonly HashSet<string> Targets = ["promotion_queue"];

    [Fact]
    public void CleanRequest_OnlyTheOutboxChanged_Passes() =>
        ApplyCommitAssertions.IsHonestApplyCommit([Outbox], Outbox, Targets, requestRowExists: true)
            .ShouldBeTrue();

    [Fact]
    public void ConsumedTail_RequestAppliedByTheMaintenanceLoop_Passes() =>
        ApplyCommitAssertions.IsHonestApplyCommit(
                ["promotion_queue_prune_requests", "maintenance_jobs", "promotion_queue"],
                Outbox, Targets, requestRowExists: true)
            .ShouldBeTrue("the 15 s poll consumed the request in-window; the stamp proves the loop did it");

    [Fact]
    public void RogueDirectDomainWrite_WithoutRequest_Fails() =>
        ApplyCommitAssertions.IsHonestApplyCommit(["promotion_queue"], Outbox, Targets, requestRowExists: false)
            .ShouldBeFalse("a synchronous domain write with no outbox request is the ADR-0075 violation");

    [Fact]
    public void RogueNoRequest_NothingElseChanged_Fails() =>
        ApplyCommitAssertions.IsHonestApplyCommit([], Outbox, Targets, requestRowExists: false)
            .ShouldBeFalse("a verb that never wrote its request is not an honest --apply, whatever else it did");

    [Fact]
    public void ForgedRequest_WithDirectDomainWrite_NoMaintenanceStamp_Fails() =>
        ApplyCommitAssertions.IsHonestApplyCommit([Outbox, "promotion_queue"], Outbox, Targets, requestRowExists: true)
            .ShouldBeFalse("a domain mutation must carry a maintenance_jobs stamp — no stamp means the CLI wrote it");

    [Fact]
    public void UnrelatedTableChanged_Fails() =>
        ApplyCommitAssertions.IsHonestApplyCommit([Outbox, "settings"], Outbox, Targets, requestRowExists: true)
            .ShouldBeFalse("a table the verb may not touch changed at all");

    [Fact]
    public void NothingChanged_RequestAlreadyPending_Passes() =>
        ApplyCommitAssertions.IsHonestApplyCommit([], Outbox, Targets, requestRowExists: true)
            .ShouldBeTrue("a re-request against an already-pending request commits nothing new");

    [Fact]
    public void RepairVerbs_TargetEntries() =>
        ApplyCommitAssertions.IsHonestApplyCommit(
                ["repair_requests", "maintenance_jobs", "entries"],
                "repair_requests", new HashSet<string> { "entries" }, requestRowExists: true)
            .ShouldBeTrue("both repair relays mutate entries — that table is in their allowed set");
}
