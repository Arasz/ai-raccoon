using AiRaccoon.Core.Access;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Tools;
using Microsoft.AspNetCore.Mvc;

namespace AiRaccoon.Settings;

internal static class DecisionEndpoint
{
    private const string Root = "/decisions/projects/{projectId}";

    extension(WebApplication app)
    {
        internal void MapDecisions()
        {
            app.MapPost(Root + "/tasks", (string projectId, RegisterTaskRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "register_decision_task", true, gate, ct, p => service.RegisterTaskAsync(p, r.Write(), r.Title, r.SourceRef, ct)));
            app.MapPost(Root + "/records", (string projectId, RecordRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "record_decision", true, gate, ct, p => service.RecordAsync(p, r.Write(), r.Content(), r.Status, ct)));
            app.MapPost(Root + "/records/{decisionId}/amend", (string projectId, string decisionId, AmendRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "amend_decision", true, gate, ct, p => service.AmendAsync(p, r.Write(), decisionId, r.ExpectedVersion, r.ChangeReason, r.Content, ct)));
            app.MapPost(Root + "/records/{decisionId}/transition", (string projectId, string decisionId, TransitionRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "transition_decision", true, gate, ct, p => service.TransitionAsync(p, r.Write(), decisionId, r.ExpectedVersion, r.TargetStatus, r.ChangeReason, ct)));
            app.MapPost(Root + "/supersessions", (string projectId, SupersedeRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "supersede_decision", true, gate, ct, p => service.SupersedeAsync(p, r.Write(), r.ReplacementId, r.ReplacedId, r.ExpectedReplacementVersion, r.ExpectedReplacedVersion, r.Reason, ct)));
            app.MapPost(Root + "/edges", (string projectId, LinkRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "link_decisions", true, gate, ct, p => service.LinkAsync(p, r.Write(), r.SourceId, r.TargetId, r.Kind, r.Reason, r.ExpectedProjectRevision, ct)));
            app.MapPost(Root + "/edges/{edgeId}/remove", (string projectId, string edgeId, UnlinkRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "unlink_decisions", true, gate, ct, p => service.UnlinkAsync(p, r.Write(), edgeId, r.Reason, r.ExpectedProjectRevision, ct)));
            app.MapGet(Root + "/records/{decisionId}", (string projectId, string decisionId, [FromQuery] int? version, [FromQuery] bool? includeHistory, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "get_decision", false, gate, ct, p => service.GetAsync(p, decisionId, version, includeHistory ?? false, ct)));
            app.MapPost(Root + "/query", (string projectId, DecisionQuery r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "query_decisions", false, gate, ct, p => service.QueryAsync(p, r, ct)));
            app.MapPost(Root + "/precedents", (string projectId, PrecedentsRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "find_decision_precedents", false, gate, ct, p => service.PrecedentsAsync(p, r.Scenario, r.Category, r.Limit, r.IncludeInactive, ct)));
            app.MapPost(Root + "/graph", (string projectId, DecisionGraphRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "get_decision_graph", false, gate, ct, p => service.GraphAsync(p, r, ct)));
            app.MapPost(Root + "/export", (string projectId, SelectionRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "export_decisions", false, gate, ct, p => service.ExportAsync(p, r.Selector, ct)));
            app.MapPost(Root + "/preview", (string projectId, DecisionPreviewOptions r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, "preview_decisions", false, gate, ct, p => service.PreviewAsync(p, r, ct)));
        }
    }

    private static async Task<IResult> Run<T>(string projectId, string name, bool write, IToolGate gate,
        CancellationToken ct, Func<string, Task<DecisionResult<T>>> action)
    {
        try
        {
            DecisionText.Identifier(projectId, "projectId");
            var canonical = await gate.RequireAsync(projectId, write ? AccessRequirement.Write : AccessRequirement.Read, name, ct);
            var result = await action(canonical);
            return Results.Ok(await gate.WrapAsync(canonical, result, ct));
        }
        catch (DecisionRevisionConflictException ex)
        {
            return Problem(ex.Message, 409, "decision-conflict", new() { ["currentProjectRevision"] = ex.CurrentRevision });
        }
        catch (DecisionException ex)
        {
            return Problem(ex.Message, ex.Kind == "decision-not-found" ? 404 : 409, ex.Kind,
                new() { ["currentVersion"] = ex.CurrentVersion, ["currentProjectRevision"] = ex.CurrentProjectRevision });
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, 400, "invalid-argument");
        }
        catch (Exception ex) when (ToolRefusals.PrefixFor(ex) is { } kind)
        {
            return Problem(ex.Message, 422, kind);
        }
    }

    private static IResult Problem(string message, int status, string kind, Dictionary<string, object?>? details = null)
    {
        details ??= new Dictionary<string, object?>();
        details["kind"] = kind;
        return Results.Problem(detail: message, statusCode: status, extensions: details);
    }

    internal record WriteRequest(string TaskId, string SessionId, string RequestId, string? Actor = null)
    {
        public DecisionWrite Write() => new(TaskId, SessionId, RequestId, Actor);
    }
    internal sealed record RegisterTaskRequest(string TaskId, string SessionId, string RequestId, string Title, string? SourceRef = null, string? Actor = null) : WriteRequest(TaskId, SessionId, RequestId, Actor);
    internal sealed record RecordRequest(string TaskId, string SessionId, string RequestId, string Title, string Category, string Scenario, string Outcome, string Reasoning,
        DecisionStatus Status = DecisionStatus.Proposed, double? Confidence = null, string? Maker = null, IReadOnlyList<string>? Alternatives = null,
        IReadOnlyList<string>? Consequences = null, IReadOnlyList<DecisionSource>? Sources = null, DateTimeOffset? ValidFrom = null, DateTimeOffset? ValidUntil = null, string? Actor = null) : WriteRequest(TaskId, SessionId, RequestId, Actor)
    {
        public DecisionContent Content() => new(Title, Category, Scenario, Outcome, Reasoning, Confidence, Maker, Alternatives, Consequences, Sources, ValidFrom, ValidUntil);
    }
    internal sealed record AmendRequest(string TaskId, string SessionId, string RequestId, int ExpectedVersion, string ChangeReason, DecisionContent Content, string? Actor = null) : WriteRequest(TaskId, SessionId, RequestId, Actor);
    internal sealed record TransitionRequest(string TaskId, string SessionId, string RequestId, int ExpectedVersion, DecisionStatus TargetStatus, string ChangeReason, string? Actor = null) : WriteRequest(TaskId, SessionId, RequestId, Actor);
    internal sealed record SupersedeRequest(string TaskId, string SessionId, string RequestId, string ReplacementId, string ReplacedId, int ExpectedReplacementVersion, int ExpectedReplacedVersion, string Reason, string? Actor = null) : WriteRequest(TaskId, SessionId, RequestId, Actor);
    internal sealed record LinkRequest(string TaskId, string SessionId, string RequestId, string SourceId, string TargetId, DecisionEdgeKind Kind, string Reason, long ExpectedProjectRevision, string? Actor = null) : WriteRequest(TaskId, SessionId, RequestId, Actor);
    internal sealed record UnlinkRequest(string TaskId, string SessionId, string RequestId, string Reason, long ExpectedProjectRevision, string? Actor = null) : WriteRequest(TaskId, SessionId, RequestId, Actor);
    internal sealed record PrecedentsRequest(string Scenario, string? Category = null, int Limit = 5, bool IncludeInactive = false);
    internal sealed record SelectionRequest(DecisionTaskSelector? Selector = null);
}
