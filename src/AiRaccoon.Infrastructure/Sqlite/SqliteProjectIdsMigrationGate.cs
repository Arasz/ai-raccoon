using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     The server side of <see cref="IProjectIdsMigrationGate" />: reads the repair_requests
///     finished row for kind=project-ids, SELECT-only, through the same cheap open the project
///     registry uses (no migration side effects on the read path).
/// </summary>
public sealed class SqliteProjectIdsMigrationGate(ISqliteConnectionFactory factory) : IProjectIdsMigrationGate
{
    /// <inheritdoc />
    public async Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                MemorySql.HasFinishedRepairRequest,
                new { kind = RepairKinds.ProjectIds },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;
    }
}
