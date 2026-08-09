using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     Rejects /mcp requests that do not present the loopback token
///     (docs/plans/2026-08-09-mcp-loopback-token-flow.md). /observability stays open by design.
/// </summary>
internal sealed class McpTokenGate
{
    public const string HeaderName = "X-AiRaccoon-Token";

    /// <summary>JSON-RPC implementation-defined server error (-32000..-32099).</summary>
    private const int UnauthorizedCode = -32001;

    private readonly string _body;
    private readonly byte[] _expected;
    private readonly RequestDelegate _next;

    public McpTokenGate(RequestDelegate next, string token, string tokenPath)
    {
        Guard.IsNotNull(next);
        Guard.IsNotNullOrWhiteSpace(token);
        Guard.IsNotNullOrWhiteSpace(tokenPath);
        _next = next;
        _expected = Encoding.UTF8.GetBytes(token);
        // A JSON-RPC error object, not a bare 401: ServerProbe's second discriminator is that the
        // endpoint speaks JSON-RPC at all, and naming the file makes a data-root mismatch diagnosable.
        var message = JsonEncodedText.Encode(
            $"ai-raccoon: /mcp needs the {HeaderName} header; the token is in {tokenPath}");
        _body = $$$"""{"jsonrpc":"2.0","id":null,"error":{"code":{{{UnauthorizedCode}}},"message":"{{{message}}}"}}""";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp") || IsAuthorized(context.Request))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(_body, context.RequestAborted);
    }

    /// <summary>Absent, wrong length and mismatched all take the same path.</summary>
    private bool IsAuthorized(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderName, out var presented) || presented is not [{ } value])
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), _expected);
    }
}
