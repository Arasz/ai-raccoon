using AiRaccoon.Core.Access;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Decisions;
using AiRaccoon.Tools;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace AiRaccoon.Settings;

/// <summary>The decision HTTP routes: one per decision tool plus the two import routes, behind the same gate, service and refusal mapping.</summary>
internal static class DecisionEndpoint
{
    private const string Root = "/decisions/projects/{projectId}";

    /// <summary>The gate operation name of both import routes; they have no MCP tool.</summary>
    private const string ImportSemantica = "import-semantica";

    extension(WebApplication app)
    {
        internal void MapDecisions()
        {
            // A query value the framework cannot bind is refused before the handler with an empty 400;
            // status-code pages turn it into problem details that KindBindingFailures then kinds.
            // Bodies are read by Run under BodyOptions, so an unknown or unreadable field is refused by name.
            app.UseWhen(context => context.Request.Path.StartsWithSegments("/decisions"), branch => branch.UseStatusCodePages());
            app.MapPost(Root + "/tasks", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnRegisterDecisionTask, AccessRequirement.Write, gate, ct, (RegisterTaskRequest r, string p) => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/records", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnRecordDecision, AccessRequirement.Write, gate, ct, (RecordRequest r, string p) => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/records/{decisionId}/amend", (string projectId, string decisionId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnAmendDecision, AccessRequirement.Write, gate, ct, (AmendRequest r, string p) => r.SendAsync(service, p, decisionId, ct)));
            app.MapPost(Root + "/records/{decisionId}/transition", (string projectId, string decisionId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnTransitionDecision, AccessRequirement.Write, gate, ct, (TransitionRequest r, string p) => r.SendAsync(service, p, decisionId, ct)));
            app.MapPost(Root + "/supersessions", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnSupersedeDecision, AccessRequirement.Write, gate, ct, (SupersedeRequest r, string p) => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/edges", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnLinkDecisions, AccessRequirement.Write, gate, ct, (LinkRequest r, string p) => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/edges/{edgeId}/remove", (string projectId, string edgeId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnUnlinkDecisions, AccessRequirement.Write, gate, ct, (UnlinkRequest r, string p) => r.SendAsync(service, p, edgeId, ct)));
            app.MapGet(Root + "/records/{decisionId}", (string projectId, string decisionId, [FromQuery] int? version, [FromQuery] bool? includeHistory, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, DecisionTools.TnGetDecision, AccessRequirement.Read, gate, ct, p => service.GetAsync(p, decisionId, version, includeHistory ?? false, ct)));
            app.MapPost(Root + "/query", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnQueryDecisions, AccessRequirement.Read, gate, ct, (DecisionQuery r, string p) => service.QueryAsync(p, r, ct)));
            app.MapPost(Root + "/precedents", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnFindDecisionPrecedents, AccessRequirement.Read, gate, ct, (PrecedentsRequest r, string p) => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/graph", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnGetDecisionGraph, AccessRequirement.Read, gate, ct, (DecisionGraphRequest r, string p) => service.GraphAsync(p, r, ct)));
            app.MapPost(Root + "/export", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnExportDecisions, AccessRequirement.Read, gate, ct, (SelectionRequest r, string p) => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/preview", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, DecisionTools.TnPreviewDecisions, AccessRequirement.Read, gate, ct, (DecisionPreviewOptions r, string p) => service.PreviewAsync(p, r, ct)));
            app.MapPost(Root + "/imports/semantica/preview", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, ImportSemantica, AccessRequirement.Read, gate, ct, (ImportPreviewRequest r, string p) => r.SendAsync(service, p, ct)));
            app.MapPost(Root + "/imports/semantica/apply", (string projectId, HttpRequest body, IToolGate gate, IDecisionService service, CancellationToken ct) =>
                Run(projectId, body, ImportSemantica, AccessRequirement.Write, gate, ct, (ImportApplyRequest r, string p) => r.SendAsync(service, p, ct)));
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

    /// <summary>The web defaults minimal APIs bind with, refusing a member the request type does not declare.</summary>
    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Reads the body as <typeparamref name="TRequest" /> before the gate, refusing it as invalid-argument when it is unusable.</summary>
    private static async Task<IResult> Run<TRequest, T>(string projectId, HttpRequest body, string name, AccessRequirement requirement,
        IToolGate gate, CancellationToken ct, Func<TRequest, string, Task<DecisionResult<T>>> action)
    {
        TRequest request;
        try
        {
            if (!body.HasJsonContentType())
            {
                throw new ArgumentException("The request body must be JSON (Content-Type: application/json).");
            }

            request = await body.ReadFromJsonAsync<TRequest>(BodyOptions, ct)
                      ?? throw new ArgumentException("The request body must be a JSON object.");
        }
        catch (Exception ex) when (DecisionRefusals.For(ex) is { } refusal)
        {
            return Problem(refusal);
        }

        return await Run(projectId, name, requirement, gate, ct, p => action(request, p));
    }

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
            return Problem(refusal);
        }
    }

    private static IResult Problem(DecisionRefusal refusal)
    {
        var details = new Dictionary<string, object?>(refusal.Details) { ["kind"] = refusal.Kind };
        return Results.Problem(detail: refusal.Message, statusCode: refusal.Status, extensions: details);
    }
}
