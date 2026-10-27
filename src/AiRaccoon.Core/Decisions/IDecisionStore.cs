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
    Task<IReadOnlyList<DecisionEdge>> EdgesAsync(CancellationToken cancellationToken);
    Task<DecisionReceipt?> ReceiptAsync(string requestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DecisionImport>> ImportsAsync(CancellationToken cancellationToken);
}

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
