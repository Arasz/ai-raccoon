using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Diagnostics;
using Microsoft.Net.Http.Headers;

namespace AiRaccoon.Hosting.Node;

/// <summary>
///     Rejects /mcp and /shutdown requests that do not present the loopback token, either as
///     <c>X-AiRaccoon-Token</c> or as <c>Authorization: Bearer &lt;token&gt;</c>
///     (docs/plans/2026-08-09-http-token-clients.md D3). /observability stays open by design.
/// </summary>
internal sealed class McpTokenGate
{
    public const string HeaderName = "X-AiRaccoon-Token";

    private const string BearerScheme = "Bearer";

    private const string McpPath = "/mcp";

    /// <summary>JSON-RPC implementation-defined server error (-32000..-32099).</summary>
    private const int UnauthorizedCode = -32001;

    /// <summary>Every path the token guards; anything else is open (ADR-0022 added /shutdown).</summary>
    private static readonly string[] GuardedPaths = [McpPath, ShutdownEndpoint.Path];

    private readonly string _absentBody;
    private readonly byte[] _expected;
    private readonly string _mismatchBody;
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
        // One wording for every guarded path (ADR-0022). /mcp additionally distinguishes a
        // presented-but-wrong credential (R4), because a human configures that endpoint by hand and
        // an unexpanded ${AIRACCOON_MCP_TOKEN} is a *present* one. /shutdown keeps the single body:
        // its only caller is `serve --restart` reading the token off disk, so the wording buys
        // nothing there, and ADR-0022 pins the refusal not to say whether a token exists.
        _absentBody = Body(
            $"ai-raccoon: this endpoint needs the {HeaderName} header or Authorization: Bearer; the token is in {tokenPath}");
        _mismatchBody = Body(
            $"ai-raccoon: this endpoint got a {HeaderName} or Authorization value that does not match the token in {tokenPath}");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGuarded(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (IsAuthorized(context.Request, out var credentialPresented))
        {
            await _next(context);
            return;
        }

        var diagnosable = credentialPresented && context.Request.Path.StartsWithSegments(McpPath);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(diagnosable ? _mismatchBody : _absentBody, context.RequestAborted);
    }

    private static bool IsGuarded(PathString path) => GuardedPaths.Any(guarded => path.StartsWithSegments(guarded));

    /// <summary>
    ///     One credential, two envelopes: each is checked independently so a wrong value in one
    ///     never shadows a correct value in the other.
    /// </summary>
    private bool IsAuthorized(HttpRequest request, out bool credentialPresented)
    {
        var hasCustomHeader = request.Headers.TryGetValue(HeaderName, out var custom);
        var hasAuthorizationHeader = request.Headers.TryGetValue(HeaderNames.Authorization, out var authorization);
        credentialPresented = hasCustomHeader || hasAuthorizationHeader;

        if (custom is [{ } customValue] && Matches(customValue))
        {
            return true;
        }

        return authorization is [{ } authorizationValue]
               && TryExtractBearerToken(authorizationValue, out var bearerToken)
               && Matches(bearerToken);
    }

    private bool Matches(string presented) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected);

    private static bool TryExtractBearerToken(string headerValue, out string token)
    {
        var separator = headerValue.IndexOf(' ');
        if (separator < 0 ||
            !headerValue.AsSpan(0, separator).Equals(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            token = "";
            return false;
        }

        token = headerValue[(separator + 1)..];
        return true;
    }

    private static string Body(string message)
    {
        var encoded = JsonEncodedText.Encode(message);
        return $$$"""{"jsonrpc":"2.0","id":null,"error":{"code":{{{UnauthorizedCode}}},"message":"{{{encoded}}}"}}""";
    }
}
