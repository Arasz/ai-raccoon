using AiRaccoon.Core.Workspace;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Workspace lifecycle rows in the local meta DB (raccoon_meta.db); never synced.</summary>
public sealed class SqliteWorkspaceStore(SqliteConnectionFactory factory) : IWorkspaceStore
{
    public async Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO workspaces (workspace_id, project_id, status, created_at, closed_at)
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
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE workspaces
                SET status = @status, closed_at = @closedAt
                WHERE workspace_id = @workspaceId AND project_id = @projectId
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

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenMetaAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS workspaces (
                    workspace_id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    closed_at INTEGER
                )
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "CREATE INDEX IF NOT EXISTS idx_workspaces_project ON workspaces(project_id)",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
