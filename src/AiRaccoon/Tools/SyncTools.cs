using System.ComponentModel;
using AiRaccoon.Core.Access;
using AiRaccoon.Infrastructure.Sync;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tool over SyncService — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class SyncTools(
    ISyncService sync,
    ISyncCloudStoreFactory syncFactory,
    IToolGate gate)
{
    private const string TnMemorySync = "memory_sync";

    [McpServerTool(Name = TnMemorySync)]
    [Description(
        "Syncs the bank's committed contexts (shared + project:<id>) to cloud object storage. " +
        "Configure with `ai-raccoon settings sync add s3 <url> --bucket <name>` or `ai-raccoon settings sync add azure " +
        "<container>` (settings table); add `--cli` to use the machine's az/aws CLI login instead of " +
        "stored secrets.")]
    public async Task<ApiEnvelope<SyncToolResult>> Sync(
        [Description("The project id.")] string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemorySync, cancellationToken);

        // Reads the configured objectKey override, if any — SyncService owns both the
        // IsConfigured decision and the default objectKey naming convention now.
        var syncSettings = await syncFactory.ReadOptionsAsync(cancellationToken);
        var result = await sync.MemorySyncAsync(canonical, syncSettings.ObjectKey, cancellationToken);
        var syncResult = new SyncToolResult(result.Sent, result.Received, result.Reindexed);
        var envelope = await gate.WrapAsync(canonical, syncResult, cancellationToken);
        return envelope;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SyncToolResult(int Sent, int Received, int Reindexed);
}
