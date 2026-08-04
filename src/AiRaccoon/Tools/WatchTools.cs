using AiRaccoon.Access;
using AiRaccoon.Core.Watch;
using AiRaccoon.Observability;

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IWatchService — no business logic here (spec §6.1).</summary>
public sealed class WatchTools(
    IWatchService watch,
    IMemoryAccessGuard access,
    ToolCallMetrics observability)
{
    private const string TN_WatchAdd = "memory_watch_add";
    private const string TN_WatchStatus = "memory_watch_status";
    private const string TN_WatchRemove = "memory_watch_remove";

    // TDD red scaffold: the S6 green commit replaces these stubs with the real
    // guard + service calls. The discards keep the scaffold compiling under
    // TreatWarningsAsErrors (unread primary-ctor params are CS9113 errors).
    public Task<WatchAddResult> Add(string projectId, string path, CancellationToken cancellationToken = default)
    {
        _ = (watch, access, observability, projectId, path, cancellationToken);
        throw new NotImplementedException();
    }

    public Task<WatchStatusResult> Status(string projectId, CancellationToken cancellationToken = default)
    {
        _ = (watch, access, observability, projectId, cancellationToken);
        throw new NotImplementedException();
    }

    public Task<WatchRemoveResult> Remove(string projectId, string path, CancellationToken cancellationToken = default)
    {
        _ = (watch, access, observability, projectId, path, cancellationToken);
        throw new NotImplementedException();
    }

    public sealed record WatchAddResult(string ProjectId, string Path);

    public sealed record WatchStatusResult(IReadOnlyList<WatchStatus> Watches);

    public sealed record WatchRemoveResult(string ProjectId, string Path);
}
