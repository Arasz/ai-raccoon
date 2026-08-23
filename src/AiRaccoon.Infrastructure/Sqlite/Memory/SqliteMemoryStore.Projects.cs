using AiRaccoon.Core.Projects;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>IProjectRegistry's half of SqliteMemoryStore (ADR-0089), split out to its own file for the same reason IModelMigrationStore's did.</summary>
public sealed partial class SqliteMemoryStore : IProjectRegistry
{
    /// <inheritdoc />
    public async Task RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var canonical = ProjectId.Canonicalize(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
                MemorySql.InsertProject,
                new { id = canonical, name, createdAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsRegisteredAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var canonical = ProjectId.Canonicalize(projectId);

        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                MemorySql.ProjectIsRegistered,
                new { projectId = canonical },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> HasRowsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var canonical = ProjectId.Canonicalize(projectId);

        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                MemorySql.ProjectHasRows,
                new { projectId = canonical },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;
    }
}
