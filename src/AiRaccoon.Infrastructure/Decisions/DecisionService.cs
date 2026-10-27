using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Decisions;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Decisions;

/// <summary>Registers project decisions and changes graph state through auditable transactions.</summary>
public sealed partial class DecisionService(IDecisionStore store, TimeProvider timeProvider) : IDecisionService
{
    public Task<DecisionResult<DecisionTask>> RegisterTaskAsync(string projectId, DecisionWrite write, string title,
        string? sourceRef = null, CancellationToken cancellationToken = default)
    {
        DecisionText.Required(title, 200, nameof(title));
        if (sourceRef is not null)
        {
            DecisionText.Required(sourceRef, 2048, nameof(sourceRef));
        }

        return MutateAsync(projectId, write, "register-task", new { title, sourceRef }, async (writer, _, ct) =>
        {
            var tasks = await writer.TasksAsync(ct);
            var existing = tasks.SingleOrDefault(task => task.TaskId == write.TaskId);
            if (existing is not null)
            {
                if (existing.Title != title || existing.SourceRef != sourceRef)
                {
                    throw Conflict("Task metadata is immutable; use its original title and source reference.");
                }

                return new Mutation<DecisionTask>(existing, false);
            }

            var task = new DecisionTask(write.TaskId, title, tasks.Count == 0 ? 1 : checked(tasks.Max(task => task.RegisteredOrder) + 1), timeProvider.GetUtcNow(), sourceRef);
            await writer.AddTaskAsync(task, ct);
            return new Mutation<DecisionTask>(task);
        }, cancellationToken, requireTask: false);
    }

    public Task<DecisionResult<DecisionVersion>> RecordAsync(string projectId, DecisionWrite write, DecisionContent content,
        DecisionStatus status = DecisionStatus.Proposed, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(content);
        Guard.IsTrue(status is DecisionStatus.Proposed or DecisionStatus.Accepted, nameof(status));
        return MutateAsync(projectId, write, "record", new { content, status }, async (writer, revision, ct) =>
        {
            var now = timeProvider.GetUtcNow();
            var id = Guid.CreateVersion7(now).ToString();
            var version = new DecisionVersion(id, 1, write.TaskId, revision, content, status, now, write.Actor, write.SessionId, null);
            await writer.AddRecordAsync(new DecisionRecord(id, write.TaskId, 1, now), ct);
            await writer.AddVersionAsync(version, ct);
            return new Mutation<DecisionVersion>(version);
        }, cancellationToken);
    }

    public Task<DecisionResult<DecisionDetails>> GetAsync(string projectId, string decisionId, int? version = null,
        bool includeHistory = false, CancellationToken cancellationToken = default)
    {
        DecisionText.Identifier(decisionId, nameof(decisionId));
        if (version.HasValue)
        {
            Guard.IsGreaterThan(version.Value, 0, nameof(version));
        }

        return store.ReadAsync(projectId, async (reader, ct) =>
        {
            var record = await reader.RecordAsync(decisionId, ct) ?? throw NotFound("Decision");
            var history = await reader.VersionsAsync(decisionId, ct);
            var wanted = version ?? record.CurrentVersion;
            var selected = history.SingleOrDefault(item => item.Version == wanted) ?? throw NotFound("Decision version");
            return new DecisionResult<DecisionDetails>(projectId, await reader.RevisionAsync(ct),
                new DecisionDetails(record, includeHistory ? history : [selected], wanted));
        }, cancellationToken);
    }

    private Task<DecisionResult<T>> MutateAsync<T>(string projectId, DecisionWrite write, string operation, object payload,
        Func<IDecisionWriter, long, CancellationToken, Task<Mutation<T>>> action, CancellationToken cancellationToken,
        bool requireTask = true)
    {
        Guard.IsNotNull(write);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { operation, write, payload }))));
        return store.WriteAsync(projectId, async (writer, ct) =>
        {
            var receipt = await writer.ReceiptAsync(write.RequestId, ct);
            if (receipt is not null)
            {
                if (receipt.PayloadHash != hash)
                {
                    throw new DecisionException("decision-request-conflict", "This requestId already identifies a different command. Use a new requestId.");
                }

                return JsonSerializer.Deserialize<DecisionResult<T>>(receipt.ResultJson)! with { Replayed = true };
            }

            if (requireTask && !(await writer.TasksAsync(ct)).Any(task => task.TaskId == write.TaskId))
            {
                throw NotFound("Task; register it before recording a decision");
            }

            var previous = await writer.RevisionAsync(ct);
            var next = checked(previous + 1);
            var mutation = await action(writer, next, ct);
            var revision = mutation.Changed ? next : previous;
            if (mutation.Changed)
            {
                await writer.SetRevisionAsync(revision, ct);
            }

            var result = new DecisionResult<T>(projectId, revision, mutation.Value, !mutation.Changed);
            await writer.AddReceiptAsync(write.RequestId, new DecisionReceipt(hash, JsonSerializer.Serialize(result), revision), ct);
            return result;
        }, cancellationToken);
    }

    private sealed record Mutation<T>(T Value, bool Changed = true);
    private static DecisionException NotFound(string resource) => new("decision-not-found", $"{resource} was not found in this project.");
    private static DecisionException Conflict(string message, int? version = null, long? revision = null) => new("decision-conflict", message, version, revision);

    public Task<DecisionResult<DecisionVersion>> AmendAsync(string projectId, DecisionWrite write, string decisionId,
        int expectedVersion, string changeReason, DecisionContent content, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(content);
        DecisionText.Required(changeReason, 2000, nameof(changeReason));
        return MutateAsync(projectId, write, "amend", new { decisionId, expectedVersion, changeReason, content }, async (writer, revision, ct) =>
        {
            var current = await CurrentAsync(writer, decisionId, expectedVersion, ct);
            try
            {
                new DecisionState(current.Status).EnsureCanAmend();
            }
            catch (InvalidOperationException exception)
            {
                throw new DecisionException("decision-invalid-transition", exception.Message);
            }

            var amended = current with
            {
                Version = checked(current.Version + 1), TaskId = write.TaskId, ProjectRevision = revision,
                Content = content, RecordedAt = timeProvider.GetUtcNow(), Actor = write.Actor,
                SessionId = write.SessionId, ChangeReason = changeReason
            };
            await writer.AddVersionAsync(amended, ct);
            return new Mutation<DecisionVersion>(amended);
        }, cancellationToken);
    }

    private static async Task<DecisionVersion> CurrentAsync(IDecisionReader reader, string decisionId,
        int? expectedVersion, CancellationToken cancellationToken)
    {
        DecisionText.Identifier(decisionId, nameof(decisionId));
        if (expectedVersion.HasValue)
        {
            Guard.IsGreaterThan(expectedVersion.Value, 0, nameof(expectedVersion));
        }

        var record = await reader.RecordAsync(decisionId, cancellationToken) ?? throw NotFound("Decision");
        if (expectedVersion.HasValue && record.CurrentVersion != expectedVersion)
        {
            throw Conflict("The decision changed; read its current version and retry with a new requestId.", record.CurrentVersion);
        }

        return (await reader.VersionsAsync(decisionId, cancellationToken)).SingleOrDefault(version => version.Version == record.CurrentVersion)
            ?? throw NotFound("Current decision version");
    }
    private DateTimeOffset Now => timeProvider.GetUtcNow();
    private IDecisionStore Store => store;
}
