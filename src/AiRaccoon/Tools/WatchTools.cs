using System.ComponentModel;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Watch;
using AiRaccoon.Observability;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IWatchService — no business logic here (see docs/work/features-agent-memory/spec-issue-1.md §6.1).</summary>
public sealed class WatchTools(
    IWatchService watch,
    IMemoryAccessGuard access,
    ToolCallMetrics observability)
{
    private const string TnWatchAdd = "memory_watch_add";
    private const string TnWatchStatus = "memory_watch_status";
    private const string TnWatchRemove = "memory_watch_remove";

    private static void RequireProjectId(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new McpException("invalid-params: project_id is required");
        }
    }

    private async Task RequireAsync(string projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken) =>
        await access.EnsureAsync(projectId, requirement, toolName, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = TnWatchAdd)]
    [Description(
        "Registers a file or directory to be mirrored into the project's memory. Watching must be enabled and the path inside the scope allowlist — both configured via the CLI ('ai-raccoon watch enable' / 'watch scope add'). Already-watched paths are a no-op. Returns immediately — the initial scan runs in the background (status reports scanning).")]
    public async Task<WatchAddResult> Add(
        [Description("The project id; watches are scoped to a project.")]
        string projectId,
        [Description("Absolute path of the file or directory to watch.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnWatchAdd, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnWatchAdd, cancellationToken);

            try
            {
                await watch.AddAsync(projectId, path, cancellationToken);
            }
            catch (WatchDisabledException ex)
            {
                activity.RecordError(ex);
                throw new McpException($"watching-disabled: {ex.Message}");
            }
            catch (PathOutsideScopeException ex)
            {
                activity.RecordError(ex);
                throw new McpException($"path-outside-scope: {ex.Message}");
            }
            catch (PathNotFound ex)
            {
                activity.RecordError(ex);
                throw new McpException($"path-not-found: {ex.Message}");
            }

            activity.RecordInvocation();
            return new WatchAddResult(projectId, path);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnWatchStatus)]
    [Description(
        "Lists the project's registered watches with their live state (scanning/healthy/retrying/stopped), last error and last sync; empty list when none. Available in every access tier.")]
    public async Task<WatchStatusResult> Status(
        [Description("The project id.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnWatchStatus, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TnWatchStatus, cancellationToken);

            var states = await watch.StatusAsync(projectId, cancellationToken);
            activity.RecordInvocation();
            return new WatchStatusResult(states);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    [McpServerTool(Name = TnWatchRemove)]
    [Description("Stops watching a path for the project and removes its registration; a non-existent watch is a no-op.")]
    public async Task<WatchRemoveResult> Remove(
        [Description("The project id.")] string projectId,
        [Description("Absolute path of the watched file or directory.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        using var activity = new ToolExecutionActivity(observability, TnWatchRemove, projectId);
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnWatchRemove, cancellationToken);

            await watch.RemoveAsync(projectId, path, cancellationToken);
            activity.RecordInvocation();
            return new WatchRemoveResult(projectId, path);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity.RecordError(ex);
            throw;
        }
    }

    public sealed record WatchAddResult(string ProjectId, string Path);

    public sealed record WatchStatusResult(IReadOnlyList<WatchStatus> Watches);

    public sealed record WatchRemoveResult(string ProjectId, string Path);
}
