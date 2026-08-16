namespace AiRaccoon.Core.Memory;

/// <summary>
///     A model-set outbox record (ADR-0076): the durable fact that an embedding-engine change was
///     committed and what it owes. <see cref="FinishedAt" /> is null for exactly as long as
///     re-embedding is owed — the only two states this record can be in, and the only transition
///     between them is <see cref="IsOpen" /> flipping false, made by the relay that drains it.
/// </summary>
public sealed record ModelMigration(string Provider, string? Model, string? BaseUrl, string Engine,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt)
{
    public bool IsOpen => FinishedAt is null;
}

/// <summary>
///     Thrown when a caller asks to start a model migration while a previous one is still open
///     (ADR-0076) — never overwrites an in-flight migration's outbox row.
/// </summary>
public sealed class ModelMigrationInProgressException(string message) : Exception(message);
