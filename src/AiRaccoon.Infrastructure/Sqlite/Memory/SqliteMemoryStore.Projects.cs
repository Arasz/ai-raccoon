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

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        if (SharedProject.Is(canonical))
        {
            await connection.RequireSharedAvailableAsync(canonical, cancellationToken);
            if (name is not null && name != SharedProject.Id)
            {
                throw new ArgumentException("shared-project-reserved: the shared target cannot be renamed.");
            }
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
                MemorySql.InsertProject,
                new { id = canonical, name, createdAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() },
                cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<bool> IsRegisteredAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var canonical = ProjectId.Canonicalize(projectId);

        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken);
        if (SharedProject.Is(canonical))
        {
            await connection.RequireSharedAvailableAsync(canonical, cancellationToken);
            return true;
        }

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                MemorySql.ProjectIsRegistered,
                new { projectId = canonical },
                cancellationToken: cancellationToken)) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> HasRowsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var canonical = ProjectId.Canonicalize(projectId);

        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                MemorySql.ProjectHasRows,
                new { projectId = canonical },
                cancellationToken: cancellationToken)) > 0;
    }
}
