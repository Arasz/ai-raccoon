using AiRaccoon.Core.Isolation;
using CommunityToolkit.Diagnostics;
using Dapper;
using WorkspaceRecord = AiRaccoon.Core.Isolation.Workspace;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Workspace lifecycle rows in the memory.db workspaces table; never synced.</summary>
public sealed class SqliteWorkspaceStore(ISqliteConnectionFactory factory) : IWorkspaceStore
{
    public async Task BeginAsync(WorkspaceRecord workspace, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(workspace);
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO workspaces (id, project_id, agent_id, name, status, created_at, closed_at)
                VALUES (@workspaceId, @projectId, @agentId, @name, @status, @createdAt, NULL)
                """,
                new
                {
                    workspaceId = workspace.Id,
                    projectId = workspace.ProjectId,
                    agentId = workspace.AgentId,
                    name = workspace.Name,
                    status = workspace.Status.ToString(),
                    createdAt = startedAt.ToUnixTimeSeconds()
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
        CancellationToken cancellationToken = default) =>
        await TryCloseAsync(projectId, workspaceId, status, closedAt, cancellationToken).ConfigureAwait(false);

    /// <summary>The real compare-and-swap (WP5b/A-F7): conditional on the row still being Active,
    /// so a concurrent consolidate and discard on the same workspace can never both succeed.</summary>
    public async Task<bool> TryCloseAsync(string projectId, string workspaceId, WorkspaceStatus status,
        DateTimeOffset closedAt, CancellationToken cancellationToken = default)
    {
        if (status == WorkspaceStatus.Active)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status,
                "A workspace cannot be closed into the Active status.");
        }

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE workspaces
                SET status = @status, closed_at = @closedAt
                WHERE id = @workspaceId AND project_id = @projectId AND status = 'Active'
                """,
                new
                {
                    workspaceId,
                    projectId,
                    status = status.ToString(),
                    closedAt = closedAt.ToUnixTimeSeconds()
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<WorkspaceRecord> RequireActiveAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QueryFirstOrDefaultAsync<Row>(
                new CommandDefinition(MemorySql.SelectWorkspace, new { workspaceId, projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (row?.Status != WorkspaceStatus.Active.ToString())
        {
            throw new UnknownWorkspaceException(workspaceId, projectId);
        }

        return new WorkspaceRecord(workspaceId, projectId, WorkspaceStatus.Active, row.AgentId, row.Name);
    }

    private sealed class Row
    {
        public string Status { get; init; } = "";

        public string? AgentId { get; init; }

        public string? Name { get; init; }
    }
}
