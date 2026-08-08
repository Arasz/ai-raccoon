using AiRaccoon.Core.Workspace;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Workspace lifecycle rows in the memory.db workspaces table; never synced.</summary>
public sealed class SqliteWorkspaceStore(SqliteConnectionFactory factory) : IWorkspaceStore
{
    public async Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO workspaces (id, project_id, status, created_at, closed_at)
                VALUES (@workspaceId, @projectId, @status, @createdAt, NULL)
                """,
                new
                {
                    workspaceId,
                    projectId,
                    status = WorkspaceStatus.Active.ToString(),
                    createdAt = startedAt.ToUnixTimeSeconds()
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE workspaces
                SET status = @status, closed_at = @closedAt
                WHERE id = @workspaceId AND project_id = @projectId
                """,
                new
                {
                    workspaceId,
                    projectId,
                    status = status.ToString(),
                    closedAt = closedAt.ToUnixTimeSeconds()
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RequireActiveAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var status = await connection.QueryFirstOrDefaultAsync<string?>(
                new CommandDefinition(MemorySql.SelectWorkspaceStatus, new { workspaceId, projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (status != WorkspaceStatus.Active.ToString())
        {
            throw new UnknownWorkspaceException(workspaceId, projectId);
        }
    }
}
