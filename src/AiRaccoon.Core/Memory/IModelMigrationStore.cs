namespace AiRaccoon.Core.Memory;

/// <summary>
///     Starts an embedding-model migration: an outbox transaction (ADR-0076) commits the new engine
///     settings and a durable migration record together, then returns without re-embedding — a
///     separate relay (the maintenance loop's on-demand job) drains it. Split out of
///     <see cref="IMemoryStore" /> for the same reason <see cref="ISettingsStore" /> was (ADR-0075):
///     the CLI reaches it through the server instead of opening the bank.
/// </summary>
public interface IModelMigrationStore
{
    /// <summary>
    ///     Commits the engine settings and, only when the engine fingerprint actually changed, a
    ///     migration-started record plus marking the bank's embedded rows pending. Throws
    ///     <see cref="ModelMigrationInProgressException" /> when a previous migration is still open.
    /// </summary>
    Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default);
}
