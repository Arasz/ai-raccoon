namespace AiRaccoon.Core.Projects;

/// <summary>
///     Which project ids exist (ADR-0089 decisions 1/3/5) — a directory, not a gate. Split out of
///     <see cref="IMemoryStore" /> for the same reason <see cref="IModelMigrationStore" /> was.
/// </summary>
public interface IProjectRegistry
{
    /// <summary>
    ///     Registers <paramref name="projectId" /> (canonicalized first), idempotently. First-write-wins:
    ///     re-registering an already-registered id leaves the existing row's <paramref name="name" /> untouched.
    /// </summary>
    Task RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default);

    /// <summary>True when the canonical form of <paramref name="projectId" /> has a registry row.</summary>
    Task<bool> IsRegisteredAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     True when the bank holds any row belonging to <paramref name="projectId" /> (ADR-0046
    ///     membership) — the legacy-compatibility half of ADR-0089 decision 3.
    /// </summary>
    Task<bool> HasRowsAsync(string projectId, CancellationToken cancellationToken = default);
}
