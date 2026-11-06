namespace AiRaccoon.Core.Decisions;

/// <summary>Executes project-owned reads and writes in one consistent transaction.</summary>
public interface IDecisionStore
{
    Task<T> ReadAsync<T>(string projectId, Func<IDecisionReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken = default);
    Task<T> WriteAsync<T>(string projectId, Func<IDecisionWriter, CancellationToken, Task<T>> write, CancellationToken cancellationToken = default);
}

public interface IDecisionReader
{
    Task<long> RevisionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionTask>> TasksAsync(CancellationToken cancellationToken);
    Task<DecisionRecord?> RecordAsync(string decisionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionRecord>> RecordsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionVersion>> VersionsAsync(string decisionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionVersion>> CurrentVersionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionVersion>> AllVersionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionSearchHit>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>One page of the current versions <paramref name="filter" /> keeps, and how many it keeps before AfterId and Take.</summary>
    Task<DecisionCurrentPage> FindCurrentAsync(DecisionCurrentFilter filter, CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionEdge>> EdgesAsync(CancellationToken cancellationToken);
    Task<DecisionReceipt?> ReceiptAsync(string requestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionImport>> ImportsAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Filters over current versions. TaskIds keeps decisions a task originated, versioned, or created or removed an
///     outgoing edge of; Search keeps full-text hits and ranks them; ActiveAt keeps versions valid at that instant. The
///     page is ordered by decision id, or by rank then decision id when ByRank, and starts after AfterId.
/// </summary>
public sealed record DecisionCurrentFilter(string? Search = null, IReadOnlyCollection<string>? TaskIds = null, string? Category = null,
    IReadOnlyCollection<DecisionStatus>? Statuses = null, DateTimeOffset? ActiveAt = null, string? AfterId = null, int Take = 20, bool ByRank = false);

public sealed record DecisionCurrentHit(DecisionVersion Version, double? Rank);

public sealed record DecisionCurrentPage(IReadOnlyList<DecisionCurrentHit> Page, int Total);

public interface IDecisionWriter : IDecisionReader
{
    Task SetRevisionAsync(long revision, CancellationToken cancellationToken);
    Task AddTaskAsync(DecisionTask task, CancellationToken cancellationToken);
    Task AddRecordAsync(DecisionRecord decision, CancellationToken cancellationToken);
    Task AddVersionAsync(DecisionVersion version, CancellationToken cancellationToken);
    Task AddEdgeAsync(DecisionEdge edge, CancellationToken cancellationToken);
    Task RemoveEdgeAsync(DecisionEdge edge, CancellationToken cancellationToken);
    Task AddReceiptAsync(string requestId, DecisionReceipt receipt, CancellationToken cancellationToken);
    Task AddImportAsync(DecisionImport import, CancellationToken cancellationToken);
}
