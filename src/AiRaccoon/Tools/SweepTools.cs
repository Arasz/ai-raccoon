using System.ComponentModel;
using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Infrastructure.Degradation;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tool over SweepService — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class SweepTools(
    SweepService sweeper,
    ForgettingPolicyService knobs,
    ToolGate gate)
{
    private const string TnMemorySweep = "memory_sweep";

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

        var threshold = await knobs.GetSweepThresholdAsync(projectId, cancellationToken);
        var outcome = await sweeper.SweepAsync(projectId, threshold, dryRun, cancellationToken);
        var result = new SweepResult(outcome.Candidates, outcome.DeletedHashes);
        var envelope = await gate.WrapAsync(projectId, result, cancellationToken);
        return envelope;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SweepResult(IReadOnlyList<SweepCandidate> Candidates, IReadOnlyList<string> Deleted);
}
