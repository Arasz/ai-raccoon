using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     Recognizes an ai-raccoon MCP server on an endpoint: POST /mcp with an MCP Accept header and a
///     non-JSON body; recognized iff status ∈ {400,401,405,406} and the body mentions jsonrpc.
///     2 attempts, 1s timeout each (docs/plans/2026-08-06-http-serve-mode-plan.md R14).
/// </summary>
public sealed class ServerProbe : IServerProbe
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ServerProbe(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    /// <summary>Probe over one pre-configured client (tests route it to an in-memory host).</summary>
    public ServerProbe(HttpClient httpClient) : this(new SingleClientFactory(httpClient))
    {
    }

    private const int Attempts = 2;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);

    private static readonly HashSet<HttpStatusCode> FailureCodes =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotAcceptable
    ];

    public Task<bool> RespondsAsync(int port, CancellationToken ctx) => RespondsAsync(EndpointFor(port), ctx);

    public async Task<bool> RespondsAsync(Uri endpoint, CancellationToken ctx)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ctx);
                attemptCts.CancelAfter(RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = new StringContent("x", Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                using var response = await _httpClientFactory.CreateClient(nameof(ServerProbe)).SendAsync(request, attemptCts.Token);

                var body = await response.Content.ReadAsStringAsync(attemptCts.Token);
                if (body.Contains("jsonrpc", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Connection refused: not an ai-raccoon server.
            }
            catch (OperationCanceledException) when (!ctx.IsCancellationRequested)
            {
                // The probe's own timeout, not the caller's token: a miss, not a cancellation.
            }
        }

        return false;
    }

    public static Uri EndpointFor(int port) => new($"http://127.0.0.1:{port}/mcp");

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
