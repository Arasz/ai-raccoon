using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Rating;

public enum SourceChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

/// <summary>Extension contract; ordered hooks run around every store operation. See ADR-0013 for the hook surface.</summary>
public interface IMemoryExtension
{
    string Name { get; }

    Task OnWriteAsync(WriteContext context, CancellationToken cancellationToken);

    Task OnSearchAsync(SearchContext context, CancellationToken cancellationToken);

    Task OnDeleteAsync(DeleteContext context, CancellationToken cancellationToken);

    /// <summary>Observes one processed source change (watch mirror); extensions that do not care inherit a no-op.</summary>
    Task OnSourceChangedAsync(SourceChangedContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record WriteContext(
    string ProjectId,
    string Content,
    string? Context = null,
    string? AgentId = null,
    string? WorkspaceId = null);

public sealed record SearchContext(
    string ProjectId,
    string Query,
    IReadOnlyList<MemorySearchResult> Results,
    string? WorkspaceId = null,
    int Limit = 20,
    double MinScore = 0.7);

public sealed record DeleteContext(string ProjectId, string? Hash = null, string? Context = null, string? Path = null);

public sealed record SourceChangedContext(string ProjectId, string Path, SourceChangeKind ChangeKind);
