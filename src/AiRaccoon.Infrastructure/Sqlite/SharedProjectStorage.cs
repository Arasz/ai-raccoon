using AiRaccoon.Core.Projects;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Refuses reserved-target activation over a pre-existing private shared owner.</summary>
internal static class SharedProjectStorage
{
    public static async Task ActivateSharedAsync(this SqliteConnection connection, string projectId,
        ISharedProjectScope scope, CancellationToken cancellationToken)
    {
        if (scope.IsReserved(projectId))
        {
            await connection.InWriteTransactionAsync(async () =>
            {
                await connection.RequireSharedAvailableAsync(projectId, scope, cancellationToken);
                await connection.MarkSharedActiveAsync(cancellationToken);
            }, cancellationToken);
        }
    }

    public static Task MarkSharedActiveAsync(this SqliteConnection connection, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Sql.Def("INSERT INTO settings(key, value) VALUES (@key, '1') ON CONFLICT(key) DO UPDATE SET value = '1'",
            new { key = SharedProjectScope.WatchActivationKey }, cancellationToken));

    public static async Task RequireSharedAvailableAsync(this SqliteConnection connection,
        string projectId, ISharedProjectScope scope, CancellationToken cancellationToken)
    {
        if (!scope.IsReserved(projectId))
        {
            return;
        }

        var conflict = await connection.ExecuteScalarAsync<bool>(Sql.Def("""
            SELECT EXISTS(SELECT 1 FROM projects WHERE id = @projectId)
                OR EXISTS(SELECT 1 FROM entries WHERE project_id = @projectId AND scope IS NOT 'shared')
                OR EXISTS(SELECT 1 FROM code_entries WHERE project_id = @projectId)
                OR EXISTS(SELECT 1 FROM workspaces WHERE project_id = @projectId)
                OR ((EXISTS(SELECT 1 FROM watches WHERE project_id = @projectId)
                    OR EXISTS(SELECT 1 FROM watch_files WHERE project_id = @projectId))
                    AND NOT EXISTS(SELECT 1 FROM settings WHERE key = @activationKey AND value = '1'))
            """, new { projectId, activationKey = SharedProjectScope.WatchActivationKey }, cancellationToken));
        if (conflict)
        {
            throw new ArgumentException("shared-project-conflict: legacy private data or registration uses 'shared'; repair that owner before using the reserved target. No data was published.");
        }
    }
}
