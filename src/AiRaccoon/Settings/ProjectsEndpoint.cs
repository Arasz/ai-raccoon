using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves the control-plane project directory: name lookup, id check and registration, so
///     `project id get|check|register` never opens the bank. Guarded by <see cref="McpTokenGate" />
///     like every other route.
/// </summary>
internal static class ProjectsEndpoint
{
    extension(WebApplication webApplication)
    {
        internal void MapProjects()
        {
            webApplication.MapGet(ProjectsProtocol.Path,
                async (string? name, IProjectDirectory directory, CancellationToken ctx) =>
                    string.IsNullOrWhiteSpace(name)
                        ? Results.BadRequest("ai-raccoon: pass a non-blank ?name=")
                        : Results.Ok(new ProjectIdsResponse(await directory.FindByNameAsync(name, ctx))));

            webApplication.MapGet(ProjectsProtocol.CheckPath,
                async (string? id, IProjectDirectory directory, CancellationToken ctx) =>
                {
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        return Results.BadRequest("ai-raccoon: pass a non-blank ?id=");
                    }

                    var check = await directory.CheckAsync(id, ctx);
                    return Results.Ok(new ProjectCheckResponse(check.Status, check.ProjectId));
                });

            webApplication.MapPost(ProjectsProtocol.Path,
                async (ProjectRegisterRequest request, IProjectDirectory directory, CancellationToken ctx) =>
                {
                    if (string.IsNullOrWhiteSpace(request.ProjectId) || (request.Name is not null && string.IsNullOrWhiteSpace(request.Name)))
                    {
                        return Results.BadRequest("ai-raccoon: a project id is required and a name, when given, must not be blank");
                    }

                    var registration = await directory.RegisterAsync(request.ProjectId, request.Name, ctx);
                    return Results.Ok(new ProjectRegisterResponse(registration.Outcome, registration.ProjectId));
                });
        }
    }
}
