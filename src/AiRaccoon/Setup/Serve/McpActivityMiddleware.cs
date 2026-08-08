namespace AiRaccoon.Setup.Serve;

/// <summary>
///     Counts /mcp requests as watchdog activity; other paths (404s) never signal
///     (docs/plans/2026-08-06-http-serve-mode-plan.md R4).
/// </summary>
public sealed class McpActivityMiddleware
{
    private readonly RequestDelegate _next;

    public McpActivityMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IActivitySignaler signaler)
    {
        if (context.Request.Path == "/mcp")
        {
            signaler.NotifyActivity();
        }

        await _next(context);
    }
}
