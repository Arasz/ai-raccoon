using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     Recognizes an ai-raccoon MCP server on an endpoint: POST /mcp with an MCP Accept header and a
///     non-JSON body; recognized iff status ∈ {400,401,405,406} and the body mentions jsonrpc.
///     2 attempts, 1s timeout each (docs/plans/2026-08-06-http-serve-mode-plan.md R14).
/// </summary>
internal sealed class ServerProbe(HttpClient httpClient)
{
    private const int Attempts = 2;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>A probe over its own client, timed out at the R14 1s per attempt.</summary>
    public static ServerProbe ForLoopback() => new(new HttpClient { Timeout = Timeout });

    public static Uri EndpointFor(int port) => new($"http://127.0.0.1:{port}/mcp");

    public Task<bool> RespondsAsync(int port, CancellationToken cancellationToken) =>
        RespondsAsync(EndpointFor(port), cancellationToken);

    public async Task<bool> RespondsAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    // JSON content type with a non-JSON body: the MCP endpoint answers 400
                    // with a JSON-RPC error body (415 only when the content type is wrong).
                    Content = new StringContent("x", Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
                };
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                using var response = await httpClient.SendAsync(request, cancellationToken);
                // 401 is in the set on purpose: the probe carries no token, so a token-guarded
                // server — or one under a different data root — must still be recognized
                // (docs/plans/2026-08-09-mcp-loopback-token-flow.md).
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                    or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotAcceptable)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (body.Contains("jsonrpc", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Connection refused or probe timeout: not an ai-raccoon server.
            }
        }

        return false;
    }
}
