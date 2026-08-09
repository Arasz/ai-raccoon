using System.Net;
using System.Text;
using System.Text.Json;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     Restores a JSON-RPC error the response status would otherwise swallow: the SDK's streamable
///     transport rejects an error status before it reads the body, so an unknown method comes back
///     as a bare -32603 with the backend's code and message both lost
///     (docs/adr/0020-always-on-http-stdio-proxy.md). Only an error carrying an id the client can
///     correlate qualifies — see IsJsonRpcError.
/// </summary>
internal sealed class JsonRpcErrorHandler : DelegatingHandler
{
    private const string DataPrefix = "data:";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        // Driven off the body, never off the status: a genuine endpoint 404 carries no JSON-RPC
        // error and keeps failing as one, so there is no status-to-code table to mis-map.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!ContainsJsonRpcError(body))
        {
            return response;
        }

        response.StatusCode = HttpStatusCode.OK;
        response.Content = Rebuffer(response.Content, body);
        return response;
    }

    /// <summary>ReadAsStringAsync drains the network stream, so hand the SDK a replayable copy.</summary>
    private static HttpContent Rebuffer(HttpContent original, string body)
    {
        var replacement = new StringContent(body, Encoding.UTF8);
        replacement.Headers.Clear();
        foreach (var header in original.Headers)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return replacement;
    }

    /// <summary>The body itself, then each SSE data line — the backend frames errors either way.</summary>
    private static bool ContainsJsonRpcError(string body) => Candidates(body).Any(IsJsonRpcError);

    private static IEnumerable<string> Candidates(string body)
    {
        yield return body.Trim();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                yield return trimmed[DataPrefix.Length..].Trim();
            }
        }
    }

    /// <summary>
    ///     A JSON-RPC error answering a request this client sent. The id is the whole of that test:
    ///     rewritten to 200, an error correlating with nothing is dropped by the SDK, which turns the
    ///     gate's 401 into silence instead of the verdict it wrote (McpTokenGate).
    /// </summary>
    private static bool IsJsonRpcError(string candidate)
    {
        if (candidate.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("jsonrpc", out _) &&
                   Correlates(document.RootElement) &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>JSON-RPC ids are strings or numbers; absent and null name no request.</summary>
    private static bool Correlates(JsonElement root) =>
        root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
}
