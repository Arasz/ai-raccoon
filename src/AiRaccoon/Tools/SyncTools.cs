using System.ComponentModel;
using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Access;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Observability;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tool over SyncService — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class SyncTools(
    SyncService sync,
    SyncCloudStoreFactory syncFactory,
    ToolGate gate,
    ToolCallMetrics observability)
{
    private const string TnMemorySync = "memory_sync";

    [McpServerTool(Name = TnMemorySync)]
    [Description(
        "Syncs the bank's committed contexts (shared + project:<id>) to cloud object storage. " +
        "Configure with `ai-raccoon sync add s3 <url> --bucket <name>` or `ai-raccoon sync add azure " +
        "<container>` (settings table); add `--cli` to use the machine's az/aws CLI login instead of " +
        "stored secrets.")]
    public async Task<ApiEnvelope<SyncToolResult>> Sync(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnMemorySync, projectId);
        try
        {
            await gate.RequireAsync(projectId, AccessRequirement.Write, TnMemorySync, cancellationToken);

            var syncSettings = await syncFactory.ReadOptionsAsync(cancellationToken);
            if (!syncSettings.IsConfigured)
            {
                var notConfigured = new McpException(
                    "sync-not-configured: run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add azure <container>' and enter the credentials when prompted");
                activity.RecordError(notConfigured);
                throw notConfigured;
            }

            var objectKey = syncSettings.ObjectKey ?? $"memory-{projectId}.db";
            try
            {
                var result = await sync.MemorySyncAsync(projectId, objectKey, cancellationToken);
                var syncResult = new SyncToolResult(result.Sent, result.Received, result.Reindexed);
                var envelope = await gate.WrapAsync(syncResult, cancellationToken);
                activity.RecordInvocation();
                return envelope;
            }
            catch (SyncNotConfiguredException ex)
            {
                activity.RecordError(ex);
                throw new McpException(
                    "sync-not-configured: run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add azure <container>' and enter the credentials when prompted");
            }
            catch (SyncAuthFailedException ex)
            {
                activity.RecordError(ex);
                throw new McpException(
                    "sync-auth-failed: run 'az login' (azure --cli) or 'aws configure' / 'aws sso login' (s3 --cli), or verify the keys with 'ai-raccoon sync show'");
            }
            catch (SyncConflictException ex)
            {
                activity.RecordError(ex);
                throw new McpException(
                    "sync-conflict: remote changed during merge — retry the sync");
            }
            catch (SyncNetworkException ex)
            {
                activity.RecordError(ex);
                throw new McpException($"sync-network: {ex.Message}");
            }
            catch (SyncCorruptFileException ex)
            {
                activity.RecordError(ex);
                throw new McpException($"sync-corrupt-file: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record SyncToolResult(int Sent, int Received, int Reindexed);
}
