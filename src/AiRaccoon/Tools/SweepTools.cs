using System.ComponentModel;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Infrastructure.Degradation;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tool over SweepService — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class SweepTools(
    ISweepService sweeper,
    IForgettingPolicyService knobs,
    IToolGate gate)
{
    private const string TnMemorySweep = "memory_sweep";
    private const string TnMemorySetTtl = "memory_set_ttl";

    [McpServerTool(Name = TnMemorySweep)]
    [Description(
        "Runs memory degradation: lists (dry_run, default) or deletes entries whose rating is below the threshold and older than their per-entry TTL. Shared entries are never swept.")]
    public async Task<ApiEnvelope<SweepResult>> Sweep(
        [Description("The project id.")] string projectId,
        [Description("When true (default), report candidates without deleting.")]
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, dryRun ? AccessRequirement.Read : AccessRequirement.Destructive, TnMemorySweep, cancellationToken);

        var threshold = await knobs.GetSweepThresholdAsync(cancellationToken);
        var outcome = await sweeper.SweepAsync(projectId, threshold, dryRun, cancellationToken);
        var result = new SweepResult(outcome.Candidates, outcome.DeletedHashes);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [McpServerTool(Name = TnMemorySetTtl)]
    [Description(
        "Sets or clears one entry's per-entry TTL — the age half of the memory_sweep rule, and the only way an entry becomes sweepable at all. "
        + "A TTL is necessary but NOT sufficient: memory_sweep deletes an entry only when its rating is ALSO below the sweep threshold. "
        + "Fresh entries start at rating 0.5 against a default threshold of 0.3, so ttlDays=7 does not expire an entry at age 30 — "
        + "its rating must decay under the threshold first. The returned canEverExpire says whether that rating gate is already met; "
        + "when it is false, no amount of age will expire this entry as things stand. "
        + "Side effect: an entry carrying a TTL drops out of memory_share_extract's promotion candidates unless that call passes includeTtlRows. "
        + "Requires full access mode.")]
    public async Task<ApiEnvelope<TtlResult>> SetTtl(
        [Description("The project id.")] string projectId,
        [Description("The entry's content hash. A hash this project does not own is refused as unknown-hash.")]
        string hash,
        [Description("Days of age before the entry is sweepable, 1-36500. Null clears the TTL, so the entry never expires; 0 is rejected.")]
        int? ttlDays = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireAsync(projectId, AccessRequirement.Destructive, TnMemorySetTtl, cancellationToken);

        var result = await knobs.SetEntryTtlAsync(projectId, hash, ttlDays, cancellationToken);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SweepResult(IReadOnlyList<SweepCandidate> Candidates, IReadOnlyList<string> Deleted);
}
