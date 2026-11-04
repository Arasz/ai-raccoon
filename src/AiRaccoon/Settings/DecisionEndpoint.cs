using AiRaccoon.Core.Access;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Decisions;
using AiRaccoon.Tools;
using Microsoft.AspNetCore.Mvc;

namespace AiRaccoon.Settings;

/// <summary>The decision HTTP routes: one per decision tool, behind the same gate, service and refusal mapping.</summary>
internal static class DecisionEndpoint
{
    private const string Root = "/decisions/projects/{projectId}";

    extension(WebApplication app)
    {
        internal void MapDecisions()
        {
            // A body the framework cannot bind is refused before the handler with an empty 400;
            // status-code pages turn it into problem details that KindBindingFailures then kinds.
            app.UseWhen(context => context.Request.Path.StartsWithSegments("/decisions"), branch => branch.UseStatusCodePages());
            app.MapPost(Root + "/tasks", (string projectId, RegisterTaskRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnRegisterDecisionTask, AccessRequirement.Write, gate, ct, p => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/records", (string projectId, RecordRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnRecordDecision, AccessRequirement.Write, gate, ct, p => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/records/{decisionId}/amend", (string projectId, string decisionId, AmendRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnAmendDecision, AccessRequirement.Write, gate, ct, p => r.SendAsync(service, p, decisionId, ct)));
            app.MapPost(Root + "/records/{decisionId}/transition", (string projectId, string decisionId, TransitionRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnTransitionDecision, AccessRequirement.Write, gate, ct, p => r.SendAsync(service, p, decisionId, ct)));
            app.MapPost(Root + "/supersessions", (string projectId, SupersedeRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnSupersedeDecision, AccessRequirement.Write, gate, ct, p => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/edges", (string projectId, LinkRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnLinkDecisions, AccessRequirement.Write, gate, ct, p => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/edges/{edgeId}/remove", (string projectId, string edgeId, UnlinkRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnUnlinkDecisions, AccessRequirement.Write, gate, ct, p => r.SendAsync(service, p, edgeId, ct)));
            app.MapGet(Root + "/records/{decisionId}", (string projectId, string decisionId, [FromQuery] int? version, [FromQuery] bool? includeHistory, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnGetDecision, AccessRequirement.Read, gate, ct, p => service.GetAsync(p, decisionId, version, includeHistory ?? false, ct)));
            app.MapPost(Root + "/query", (string projectId, DecisionQuery r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnQueryDecisions, AccessRequirement.Read, gate, ct, p => service.QueryAsync(p, r, ct)));
            app.MapPost(Root + "/precedents", (string projectId, PrecedentsRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnFindDecisionPrecedents, AccessRequirement.Read, gate, ct, p => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/graph", (string projectId, DecisionGraphRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnGetDecisionGraph, AccessRequirement.Read, gate, ct, p => service.GraphAsync(p, r, ct)));
            app.MapPost(Root + "/export", (string projectId, SelectionRequest r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnExportDecisions, AccessRequirement.Read, gate, ct, p => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/preview", (string projectId, DecisionPreviewOptions r, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnPreviewDecisions, AccessRequirement.Read, gate, ct, p => service.PreviewAsync(p, r, ct)));
        }
    }

    /// <summary>
    ///     Gives a request the framework refused before the handler ran (an unreadable body, an
    ///     unknown enum name) the same invalid-argument kind the MCP tools report.
    /// </summary>
    internal static void KindBindingFailures(ProblemDetailsOptions options) =>
        options.CustomizeProblemDetails = context =>
        {
            if (context.HttpContext.Request.Path.StartsWithSegments("/decisions")
                && context.ProblemDetails.Status == StatusCodes.Status400BadRequest
                && !context.ProblemDetails.Extensions.ContainsKey("kind"))
            {
                context.ProblemDetails.Extensions["kind"] = DecisionRefusals.InvalidArgument;
            }
        };

    private static async Task<IResult> Run<T>(string projectId, string name, AccessRequirement requirement, IToolGate gate,
        CancellationToken ct, Func<string, Task<DecisionResult<T>>> action)
    {
        try
        {
            var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), requirement, name, ct);
            var result = await action(canonical);
            return Results.Ok(await gate.WrapAsync(canonical, result, ct));
        }
        catch (Exception ex) when (DecisionRefusals.For(ex) is { } refusal)
        {
            var details = new Dictionary<string, object?>(refusal.Details) { ["kind"] = refusal.Kind };
            return Results.Problem(detail: refusal.Message, statusCode: refusal.Status, extensions: details);
        }
    }
}
