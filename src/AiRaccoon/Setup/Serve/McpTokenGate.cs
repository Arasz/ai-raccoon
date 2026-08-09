using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Diagnostics;
using Microsoft.Net.Http.Headers;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     Rejects /mcp requests that do not present the loopback token, either as
///     <c>X-AiRaccoon-Token</c> or as <c>Authorization: Bearer &lt;token&gt;</c>
///     (docs/plans/2026-08-09-http-token-clients.md D3). /observability stays open by design.
/// </summary>
internal sealed class McpTokenGate
{
    public const string HeaderName = "X-AiRaccoon-Token";

    private const string BearerScheme = "Bearer";

    /// <summary>JSON-RPC implementation-defined server error (-32000..-32099).</summary>
    private const int UnauthorizedCode = -32001;

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
        _absentBody = Body(
            $"ai-raccoon: /mcp needs the {HeaderName} header or Authorization: Bearer; the token is in {tokenPath}");
        // Distinct from the absent message (R4): a presented-but-wrong credential tells the caller
        // what it actually got wrong, rather than reprinting instructions it already followed.
        _mismatchBody = Body(
            $"ai-raccoon: /mcp got a {HeaderName} or Authorization value that does not match the token in {tokenPath}");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await _next(context);
            return;
        }

        if (IsAuthorized(context.Request, out var credentialPresented))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(credentialPresented ? _mismatchBody : _absentBody, context.RequestAborted);
    }

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

    private bool Matches(string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected);

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
