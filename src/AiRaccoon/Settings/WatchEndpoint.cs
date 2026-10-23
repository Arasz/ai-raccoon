using AiRaccoon.Core.Access;
using AiRaccoon.Core.Watch;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Tools;
using Microsoft.AspNetCore.Mvc;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves `watch add|remove|status` (ADR-0075 amendment): the same <see cref="IToolGate" /> and
///     <see cref="IWatchService" /> as add_watch/remove_watch/get_watch_status, so the CLI gets the tools'
///     guards and results. A mapped refusal answers 422 problem details (RFC 9457) carrying <c>kind</c> and, for an overlap, <c>coveringPath</c>; guarded by <see cref="McpTokenGate" />.
/// </summary>
internal static class WatchEndpoint
{
    extension(WebApplication webApplication)
    {
        internal void MapWatch()
        {
            webApplication.MapPost(WatchProtocol.Path,
                (WatchAddRequest request, IToolGate gate, IWatchService watch, CancellationToken ctx) => RefusingAsync(async () =>
                {
                    var canonical = await gate.RequireAsync(request.ProjectId, AccessRequirement.Write, WatchTools.TnAddWatch, ctx);
                    var outcome = await watch.AddAsync(canonical, request.Path, ctx);
                    return Results.Ok(new WatchAddResponse(canonical, request.Path, outcome.Pruned, outcome.AbsorbedBy));
                }));

            webApplication.MapDelete(WatchProtocol.Path,
                ([FromQuery] string projectId, [FromQuery] string path, IToolGate gate, IWatchService watch, CancellationToken ctx) =>
                    RefusingAsync(async () =>
                    {
                        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Write, WatchTools.TnRemoveWatch, ctx);
                        var removed = await watch.RemoveAsync(canonical, path, ctx);
                        return Results.Ok(new WatchRemoveResponse(canonical, path, removed));
                    }));

            webApplication.MapGet(WatchProtocol.Path,
                ([FromQuery] string? projectId, IToolGate gate, IWatchService watch, IWatchRegisteredStore registered,
                    CancellationToken ctx) => RefusingAsync(async () =>
                {
                    if (projectId is null)
                    {
                        return Results.Ok(new WatchStatusResponse(await AllStatusesAsync(watch, registered, ctx)));
                    }

                    var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, WatchTools.TnGetWatchStatus, ctx);
                    return Results.Ok(new WatchStatusResponse(await watch.StatusAsync(canonical, ctx)));
                }));
        }
    }

    /// <summary>Every project holding a registration, unconditionally like `watch registered`.</summary>
    private static async Task<IReadOnlyList<WatchStatus>> AllStatusesAsync(IWatchService watch,
        IWatchRegisteredStore registered, CancellationToken ctx)
    {
        var projects = (await registered.ListWatchesAsync(ctx))
            .Select(r => r.ProjectId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var statuses = new List<WatchStatus>();
        foreach (var project in projects)
        {
            statuses.AddRange(await watch.StatusAsync(project, ctx));
        }

        return statuses;
    }

    private static async Task<IResult> RefusingAsync(Func<Task<IResult>> handle)
    {
        try
        {
            return await handle();
        }
        catch (Exception ex) when (ToolRefusals.PrefixFor(ex) is { } kind)
        {
            var extensions = new Dictionary<string, object?> { [WatchProtocol.KindExtension] = kind };
            if (ex is WatchOverlapException { CoveringPath: { } covering })
            {
                extensions[WatchProtocol.CoveringPathExtension] = covering;
            }

            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Watch refused", extensions: extensions);
        }
    }
}
