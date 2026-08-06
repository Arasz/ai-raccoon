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
        using var activity = observability.ActivitySource.StartActivity(TnWatchAdd);
        activity?.SetTag("tool", TnWatchAdd);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
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
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(WatchDisabledException));
                observability.RecordInvocation(TnWatchAdd, sw.Elapsed, true, nameof(WatchDisabledException));
                throw new McpException($"watching-disabled: {ex.Message}");
            }
            catch (PathOutsideScopeException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(PathOutsideScopeException));
                observability.RecordInvocation(TnWatchAdd, sw.Elapsed, true, nameof(PathOutsideScopeException));
                throw new McpException($"path-outside-scope: {ex.Message}");
            }
            catch (PathNotFound ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error_type", nameof(PathNotFound));
                observability.RecordInvocation(TnWatchAdd, sw.Elapsed, true, nameof(PathNotFound));
                throw new McpException($"path-not-found: {ex.Message}");
            }

            observability.RecordInvocation(TnWatchAdd, sw.Elapsed, false);
            return new WatchAddResult(projectId, path);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnWatchAdd, sw.Elapsed, true, ex.GetType().Name);
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
        using var activity = observability.ActivitySource.StartActivity(TnWatchStatus);
        activity?.SetTag("tool", TnWatchStatus);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Read, TnWatchStatus, cancellationToken);

            var states = await watch.StatusAsync(projectId, cancellationToken);
            observability.RecordInvocation(TnWatchStatus, sw.Elapsed, false);
            return new WatchStatusResult(states);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnWatchStatus, sw.Elapsed, true, ex.GetType().Name);
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
        using var activity = observability.ActivitySource.StartActivity(TnWatchRemove);
        activity?.SetTag("tool", TnWatchRemove);
        activity?.SetTag("project_id", projectId);
        var sw = Stopwatch.StartNew();
        try
        {
            RequireProjectId(projectId);
            await RequireAsync(projectId, AccessRequirement.Write, TnWatchRemove, cancellationToken);

            await watch.RemoveAsync(projectId, path, cancellationToken);
            observability.RecordInvocation(TnWatchRemove, sw.Elapsed, false);
            return new WatchRemoveResult(projectId, path);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            observability.RecordInvocation(TnWatchRemove, sw.Elapsed, true, ex.GetType().Name);
            throw;
        }
    }

    public sealed record WatchAddResult(string ProjectId, string Path);

    public sealed record WatchStatusResult(IReadOnlyList<WatchStatus> Watches);

    public sealed record WatchRemoveResult(string ProjectId, string Path);
}
