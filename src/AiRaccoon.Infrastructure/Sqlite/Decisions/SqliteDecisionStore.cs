using System.Globalization;
using System.Text.Json;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite.Decisions;

/// <summary>Project decision transactions in the existing encrypted bank.</summary>
public sealed class SqliteDecisionStore(ISqliteConnectionFactory factory, ISharedProjectScope scope) : IDecisionStore
{
    public async Task<T> ReadAsync<T>(string projectId, Func<IDecisionReader, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken = default)
    {
        DecisionText.Identifier(projectId, nameof(projectId));
        Guard.IsNotNull(read);
        scope.EnsureCanExecuteInSharedScope(projectId, "decisions");
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("BEGIN;", cancellationToken: cancellationToken));
        try
        {
            var result = await read(new Session(connection, projectId, false), cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("COMMIT;", cancellationToken: cancellationToken));
            return result;
        }
        catch
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK;", cancellationToken: CancellationToken.None));
            throw;
        }
    }

    public async Task<T> WriteAsync<T>(string projectId, Func<IDecisionWriter, CancellationToken, Task<T>> write,
        CancellationToken cancellationToken = default)
    {
        DecisionText.Identifier(projectId, nameof(projectId));
        Guard.IsNotNull(write);
        scope.EnsureCanExecuteInSharedScope(projectId, "decisions");
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        return await connection.InWriteTransactionAsync(async () =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO decision_projects(project_id,revision) VALUES(@projectId,0) ON CONFLICT(project_id) DO NOTHING",
                new { projectId }, cancellationToken: cancellationToken));
            return await write(new Session(connection, projectId, true), cancellationToken);
        }, cancellationToken);
    }

    private sealed class Session(SqliteConnection connection, string projectId, bool writable) : IDecisionWriter
    {
        private CommandDefinition Command(string sql, object? args, CancellationToken ct)
        {
            var parameters = new DynamicParameters(args);
            parameters.Add("projectId", projectId);
            return new CommandDefinition(sql, parameters, cancellationToken: ct);
        }

        private async Task<IReadOnlyList<IDictionary<string, object>>> Rows(string sql, object? args, CancellationToken ct) =>
            (await connection.QueryAsync(Command(sql, args, ct))).Select(row => (IDictionary<string, object>)row).ToArray();

        private async Task Execute(string sql, object args, CancellationToken ct)
        {
            Guard.IsTrue(writable, nameof(writable), "A read transaction cannot write decisions.");
            await connection.ExecuteAsync(Command(sql, args, ct));
        }

        public async Task<long> RevisionAsync(CancellationToken cancellationToken) =>
            await connection.ExecuteScalarAsync<long?>(Command("SELECT revision FROM decision_projects WHERE project_id=@projectId", null, cancellationToken)) ?? 0;

        public async Task<IReadOnlyList<DecisionTask>> TasksAsync(CancellationToken cancellationToken) =>
            (await Rows("SELECT * FROM decision_tasks WHERE project_id=@projectId ORDER BY registered_order DESC", null, cancellationToken))
            .Select(row => new DecisionTask(Text(row, "task_id"), Text(row, "title"), Number(row, "registered_order"), Time(row, "registered_at"), Optional(row, "source_ref"))).ToArray();

        public async Task<DecisionRecord?> RecordAsync(string decisionId, CancellationToken cancellationToken) =>
            (await Rows("SELECT * FROM decisions WHERE project_id=@projectId AND decision_id=@decisionId", new { decisionId }, cancellationToken)).Select(Record).SingleOrDefault();

        public async Task<IReadOnlyList<DecisionRecord>> RecordsAsync(CancellationToken cancellationToken) =>
            (await Rows("SELECT * FROM decisions WHERE project_id=@projectId ORDER BY decision_id", null, cancellationToken)).Select(Record).ToArray();

        public async Task<IReadOnlyList<DecisionVersion>> VersionsAsync(string decisionId, CancellationToken cancellationToken) =>
            (await Rows("SELECT * FROM decision_versions WHERE project_id=@projectId AND decision_id=@decisionId ORDER BY version", new { decisionId }, cancellationToken)).Select(Version).ToArray();

        public async Task<IReadOnlyList<DecisionEdge>> EdgesAsync(CancellationToken cancellationToken) =>
            (await Rows("SELECT * FROM decision_edges WHERE project_id=@projectId ORDER BY source_id,kind,target_id,edge_id", null, cancellationToken)).Select(Edge).ToArray();

        public async Task<DecisionReceipt?> ReceiptAsync(string requestId, CancellationToken cancellationToken) =>
            (await Rows("SELECT * FROM decision_requests WHERE project_id=@projectId AND request_id=@requestId", new { requestId }, cancellationToken))
            .Select(row => new DecisionReceipt(Text(row, "canonical_payload_hash"), Text(row, "result"), Number(row, "committed_revision"))).SingleOrDefault();

        public async Task<IReadOnlyList<DecisionImport>> ImportsAsync(CancellationToken cancellationToken) =>
            (await Rows("SELECT * FROM decision_imports WHERE project_id=@projectId ORDER BY source_namespace,external_id", null, cancellationToken))
            .Select(row => new DecisionImport(Text(row, "source_namespace"), Text(row, "external_id"), Optional(row, "decision_id"), Text(row, "task_id"), Text(row, "source_digest"), Text(row, "raw_payload"), Text(row, "mapping"))).ToArray();

        public async Task SetRevisionAsync(long revision, CancellationToken cancellationToken)
        {
            Guard.IsTrue(writable, nameof(writable));
            Guard.IsGreaterThan(revision, 0L);
            var changed = await connection.ExecuteAsync(Command(
                "UPDATE decision_projects SET revision=@revision WHERE project_id=@projectId AND revision=@previous",
                new { revision, previous = revision - 1 }, cancellationToken));
            if (changed != 1)
            {
                throw new DecisionRevisionConflictException(revision - 1, await RevisionAsync(cancellationToken));
            }
        }

        public Task AddTaskAsync(DecisionTask task, CancellationToken cancellationToken) => Execute(
            "INSERT INTO decision_tasks(project_id,task_id,title,registered_order,registered_at,source_ref) VALUES(@projectId,@TaskId,@Title,@RegisteredOrder,@at,@SourceRef)",
            new { task.TaskId, task.Title, task.RegisteredOrder, at = Iso(task.RegisteredAt), task.SourceRef }, cancellationToken);

        public Task AddRecordAsync(DecisionRecord decision, CancellationToken cancellationToken) => Execute(
            "INSERT INTO decisions(project_id,decision_id,origin_task_id,current_version,created_at) VALUES(@projectId,@DecisionId,@OriginTaskId,@CurrentVersion,@at)",
            new { decision.DecisionId, decision.OriginTaskId, decision.CurrentVersion, at = Iso(decision.CreatedAt) }, cancellationToken);

        public async Task AddVersionAsync(DecisionVersion version, CancellationToken cancellationToken)
        {
            var content = version.Content;
            await Execute("""
                INSERT INTO decision_versions(project_id,decision_id,version,task_id,project_revision,title,category,scenario,outcome,reasoning,
                    alternatives,consequences,confidence,decision_maker,status,valid_from,valid_until,recorded_at,actor,session_id,change_reason,sources)
                VALUES(@projectId,@DecisionId,@Version,@TaskId,@ProjectRevision,@Title,@Category,@Scenario,@Outcome,@Reasoning,
                    @alternatives,@consequences,@Confidence,@Maker,@status,@validFrom,@validUntil,@at,@Actor,@SessionId,@ChangeReason,@sources)
                """, new
            {
                version.DecisionId, version.Version, version.TaskId, version.ProjectRevision,
                content.Title, content.Category, content.Scenario, content.Outcome, content.Reasoning, content.Confidence, content.Maker,
                alternatives = JsonSerializer.Serialize(content.Alternatives), consequences = JsonSerializer.Serialize(content.Consequences),
                sources = JsonSerializer.Serialize(content.Sources), status = version.Status.ToString(), validFrom = Iso(content.ValidFrom), validUntil = Iso(content.ValidUntil),
                at = Iso(version.RecordedAt), version.Actor, version.SessionId, version.ChangeReason
            }, cancellationToken);
            await Execute("UPDATE decisions SET current_version=@Version WHERE project_id=@projectId AND decision_id=@DecisionId",
                new { version.Version, version.DecisionId }, cancellationToken);
        }

        public Task AddEdgeAsync(DecisionEdge edge, CancellationToken cancellationToken) => Execute("""
            INSERT INTO decision_edges(project_id,edge_id,source_id,target_id,kind,reason,task_id,created_revision,created_at,actor,session_id)
            VALUES(@projectId,@EdgeId,@SourceId,@TargetId,@kind,@Reason,@TaskId,@CreatedRevision,@at,@Actor,@SessionId)
            """, new { edge.EdgeId, edge.SourceId, edge.TargetId, kind = edge.Kind.ToString(), edge.Reason, edge.TaskId, edge.CreatedRevision, at = Iso(edge.CreatedAt), edge.Actor, edge.SessionId }, cancellationToken);

        public Task RemoveEdgeAsync(DecisionEdge edge, CancellationToken cancellationToken) => Execute("""
            UPDATE decision_edges SET removed_revision=@RemovedRevision,removed_at=@at,removal_task_id=@RemovalTaskId,
                removal_reason=@RemovalReason,removal_actor=@RemovalActor,removal_session_id=@RemovalSessionId
            WHERE project_id=@projectId AND edge_id=@EdgeId AND removed_revision IS NULL
            """, new { edge.EdgeId, edge.RemovedRevision, at = Iso(edge.RemovedAt), edge.RemovalTaskId, edge.RemovalReason, edge.RemovalActor, edge.RemovalSessionId }, cancellationToken);

        public Task AddReceiptAsync(string requestId, DecisionReceipt receipt, CancellationToken cancellationToken) => Execute("""
            INSERT INTO decision_requests(project_id,request_id,canonical_payload_hash,result,committed_revision)
            VALUES(@projectId,@requestId,@PayloadHash,@ResultJson,@CommittedRevision)
            """, new { requestId, receipt.PayloadHash, receipt.ResultJson, receipt.CommittedRevision }, cancellationToken);

        public Task AddImportAsync(DecisionImport import, CancellationToken cancellationToken) => Execute("""
            INSERT INTO decision_imports(project_id,source_namespace,external_id,decision_id,task_id,source_digest,raw_payload,mapping)
            VALUES(@projectId,@SourceNamespace,@ExternalId,@DecisionId,@TaskId,@SourceDigest,@RawPayload,@Mapping)
            """, import, cancellationToken);

        private static DecisionRecord Record(IDictionary<string, object> row) => new(Text(row, "decision_id"), Text(row, "origin_task_id"), checked((int)Number(row, "current_version")), Time(row, "created_at"));
        private static DecisionVersion Version(IDictionary<string, object> row) => new(Text(row, "decision_id"), checked((int)Number(row, "version")), Text(row, "task_id"), Number(row, "project_revision"),
            new DecisionContent(Text(row, "title"), Text(row, "category"), Text(row, "scenario"), Text(row, "outcome"), Text(row, "reasoning"),
                Optional(row, "confidence") is null ? null : Convert.ToDouble(row["confidence"], CultureInfo.InvariantCulture), Optional(row, "decision_maker"),
                JsonSerializer.Deserialize<string[]>(Text(row, "alternatives")), JsonSerializer.Deserialize<string[]>(Text(row, "consequences")), JsonSerializer.Deserialize<DecisionSource[]>(Text(row, "sources")),
                OptionalTime(row, "valid_from"), OptionalTime(row, "valid_until")),
            Enum.Parse<DecisionStatus>(Text(row, "status")), Time(row, "recorded_at"), Text(row, "actor"), Text(row, "session_id"), Optional(row, "change_reason"));
        private static DecisionEdge Edge(IDictionary<string, object> row) => new(Text(row, "edge_id"), Text(row, "source_id"), Text(row, "target_id"), Enum.Parse<DecisionEdgeKind>(Text(row, "kind")), Text(row, "reason"), Text(row, "task_id"), Number(row, "created_revision"), Time(row, "created_at"), Text(row, "actor"), Text(row, "session_id"),
            Optional(row, "removed_revision") is null ? null : Number(row, "removed_revision"), OptionalTime(row, "removed_at"), Optional(row, "removal_task_id"), Optional(row, "removal_reason"), Optional(row, "removal_actor"), Optional(row, "removal_session_id"));
        private static string Text(IDictionary<string, object> row, string key) => Convert.ToString(row[key], CultureInfo.InvariantCulture)!;
        private static string? Optional(IDictionary<string, object> row, string key) => row[key] is null or DBNull ? null : Text(row, key);
        private static long Number(IDictionary<string, object> row, string key) => Convert.ToInt64(row[key], CultureInfo.InvariantCulture);
        private static DateTimeOffset Time(IDictionary<string, object> row, string key) => DateTimeOffset.Parse(Text(row, key), CultureInfo.InvariantCulture);
        private static DateTimeOffset? OptionalTime(IDictionary<string, object> row, string key) => Optional(row, key) is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
        private static string? Iso(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
