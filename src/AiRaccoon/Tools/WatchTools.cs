using System.ComponentModel;
using System.Diagnostics;
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
    private const string TN_WatchAdd = "memory_watch_add";
    private const string TN_WatchStatus = "memory_watch_status";
    private const string TN_WatchRemove = "memory_watch_remove";

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

    [McpServerTool(Name = TN_WatchAdd)]
    [Description(
        "Registers a file or directory to be mirrored into the project's memory. Watching must be enabled and the path inside the scope allowlist — both configured via the CLI ('ai-raccoon watch enable' / 'watch scope add'). Already-watched paths are a no-op. Returns immediately — the initial scan runs in the background (status reports scanning).")]
    public async Task<WatchAddResult> Add(
        [Description("The project id; watches are scoped to a project.")]
        string projectId,
        [Description("Absolute path of the file or directory to watch.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_WatchAdd);
        activity?.SetTag("tool", TN_WatchAdd);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_WatchAdd, cancellationToken);

            try
            {
                await watch.AddAsync(projectId, path, cancellationToken);
            }
            catch (WatchDisabledException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(WatchDisabledException));
                observability.RecordInvocation(TN_WatchAdd, sw.Elapsed, true, nameof(WatchDisabledException));
                throw new McpException($"watching-disabled: {ex.Message}");
            }
            catch (PathOutsideScopeException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(PathOutsideScopeException));
                observability.RecordInvocation(TN_WatchAdd, sw.Elapsed, true, nameof(PathOutsideScopeException));
                throw new McpException($"path-outside-scope: {ex.Message}");
            }
            catch (PathNotFound ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(PathNotFound));
                observability.RecordInvocation(TN_WatchAdd, sw.Elapsed, true, nameof(PathNotFound));
                throw new McpException($"path-not-found: {ex.Message}");
            }

            observability.RecordInvocation(TN_WatchAdd, sw.Elapsed, false);
            return new WatchAddResult(projectId, path);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_WatchAdd, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_WatchStatus)]
    [Description(
        "Lists the project's registered watches with their live state (scanning/healthy/retrying/stopped), last error and last sync; empty list when none. Available in every access tier.")]
    public async Task<WatchStatusResult> Status(
        [Description("The project id.")]
        string projectId,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_WatchStatus);
        activity?.SetTag("tool", TN_WatchStatus);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TN_WatchStatus, cancellationToken);

            var states = await watch.StatusAsync(projectId, cancellationToken);
            observability.RecordInvocation(TN_WatchStatus, sw.Elapsed, false);
            return new WatchStatusResult(states);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_WatchStatus, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    [McpServerTool(Name = TN_WatchRemove)]
    [Description("Stops watching a path for the project and removes its registration; a non-existent watch is a no-op.")]
    public async Task<WatchRemoveResult> Remove(
        [Description("The project id.")]
        string projectId,
        [Description("Absolute path of the watched file or directory.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        using var activity = observability.ActivitySource.StartActivity(TN_WatchRemove);
        activity?.SetTag("tool", TN_WatchRemove);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TN_WatchRemove, cancellationToken);

            await watch.RemoveAsync(projectId, path, cancellationToken);
            observability.RecordInvocation(TN_WatchRemove, sw.Elapsed, false);
            return new WatchRemoveResult(projectId, path);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TN_WatchRemove, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    public sealed record WatchAddResult(string ProjectId, string Path);

    public sealed record WatchStatusResult(IReadOnlyList<WatchStatus> Watches);

    public sealed record WatchRemoveResult(string ProjectId, string Path);
}
