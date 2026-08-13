namespace AiRaccoon.Hosting.Watchdog;

/// <summary>
///     Counts /mcp requests as watchdog activity; other paths (404s) never signal
///     (docs/plans/2026-08-06-http-serve-mode-plan.md R4).
/// </summary>
public sealed class McpActivityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IActivitySignaler signaler)
    {
        if (context.Request.Path == "/mcp")
        {
            signaler.NotifyActivity();
        }

        await next(context);
    }
}
