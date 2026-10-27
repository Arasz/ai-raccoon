using AiRaccoon.Core.Access;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Tools;

namespace AiRaccoon.Settings;

internal static class DecisionEndpoint
{
    extension(WebApplication app)
    {
        internal void MapDecisions()
        {
            app.MapPost("/decisions/projects/{projectId}/tasks", async (string projectId, RegisterTaskRequest request,
                IToolGate gate, IDecisionService service, CancellationToken ct) =>
            {
                try
                {
                    var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, "register_decision_task", ct);
                    var result = await service.RegisterTaskAsync(canonical,
                        new DecisionWrite(request.TaskId, request.SessionId, request.RequestId), request.Title, request.SourceRef, ct);
                    return Results.Ok(result);
                }
                catch (ArgumentException ex)
                {
                    return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
                }
                catch (Exception ex) when (ToolRefusals.PrefixFor(ex) is { } kind)
                {
                    return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity,
                        extensions: new Dictionary<string, object?> { ["kind"] = kind });
                }
            });
        }
    }

    internal sealed record RegisterTaskRequest(string TaskId, string SessionId, string RequestId, string Title, string? SourceRef = null);
}
