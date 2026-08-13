namespace AiRaccoon.Infrastructure.Sqlite;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using Dapper;

public sealed class SqliteNoiseStore(SqliteConnectionFactory factory) : INoiseStore
{
    public async Task RecordNoiseAsync(MemoryWriteRequest request, string policyName, int expiresAtUnixSeconds, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string sql = @"
            INSERT INTO noise_entries 
            (request_content, project_id, source_file, detected_by_policy, expires_at, created_at) 
            VALUES 
            (@content, @projectId, @sourceFile, @policy, @expiresAt, @createdAt)";
        await connection.ExecuteAsync(sql, new { content = request.Content, projectId = request.ProjectId, sourceFile = request.SourceFile, policy = policyName, expiresAt = expiresAtUnixSeconds, createdAt = now }).ConfigureAwait(false);
    }
}
