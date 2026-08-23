using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Infrastructure.Resilience;
using CommunityToolkit.Diagnostics;
using Polly;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     Recognizes an ai-raccoon MCP server on an endpoint: POST /mcp with an MCP Accept header and a
///     non-JSON body; recognized iff the status is not transient (5xx/408/429 are retried) and the
///     body mentions jsonrpc — any other status counts, the status set is not checked (#539).
///     Uses Polly v8 resilience pipeline with exponential backoff and random jitter.
/// </summary>
public sealed class ServerProbe : IServerProbe
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly ResiliencePipeline _resiliencePipeline;

    public ServerProbe(IHttpClientFactory httpClientFactory, ResiliencePipeline? resiliencePipeline = null, TimeSpan? requestTimeout = null)
    {
        _httpClientFactory = httpClientFactory;
        _resiliencePipeline = resiliencePipeline ?? ResiliencePipelineFactory.CreateProbePipeline();
        _requestTimeout = requestTimeout ?? RequestTimeout;
        Guard.IsGreaterThan(_requestTimeout, TimeSpan.Zero);
    }

    /// <summary>Probe over one pre-configured client (tests route it to an in-memory host).</summary>
    public ServerProbe(HttpClient httpClient, ResiliencePipeline? resiliencePipeline = null, TimeSpan? requestTimeout = null)
        : this(new SingleClientFactory(httpClient), resiliencePipeline, requestTimeout)
    {
    }

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);

    public Task<bool> RespondsAsync(int port, CancellationToken ctx) => RespondsAsync(EndpointFor(port), ctx);

    public async Task<bool> RespondsAsync(Uri endpoint, CancellationToken ctx) =>
        await ProbeAsync(endpoint, ctx) is ProbeVerdict.Answered;

    public Task<ProbeVerdict> ProbeAsync(int port, CancellationToken ctx) => ProbeAsync(EndpointFor(port), ctx);

    public async Task<ProbeVerdict> ProbeAsync(Uri endpoint, CancellationToken ctx)
    {
        try
        {
            return await _resiliencePipeline.ExecuteAsync(async attemptToken =>
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ctx, attemptToken);
                attemptCts.CancelAfter(_requestTimeout);

                try
                {

                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Content = new StringContent("x", Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
                    request.Headers.Accept.ParseAdd("application/json, text/event-stream");

                    using var response = await _httpClientFactory.CreateClient(nameof(ServerProbe)).SendAsync(request, attemptCts.Token);

                    if (ResiliencePipelineFactory.IsTransientHttpStatusCode(response.StatusCode))
                    {
                        throw new HttpRequestException($"Transient HTTP status {response.StatusCode} from {endpoint}");
                    }

                    var body = await response.Content.ReadAsStringAsync(attemptCts.Token);
                    // A reply that is not ai-raccoon's still proves a listener, so it is never NotListening.
                    return body.Contains("jsonrpc", StringComparison.Ordinal) ? ProbeVerdict.Answered : ProbeVerdict.Unanswered;
                }
                catch (OperationCanceledException) when (!ctx.IsCancellationRequested)
                {
                    // The per-attempt bound expired. Surfaced as a TimeoutException so the pipeline's
                    // retry actually handles it — an OperationCanceledException escapes ExecuteAsync
                    // and spends one of three configured attempts. Guarded on the CALLER's token, not
                    // the linked one: HttpClient's own Timeout cancels an internal source, so the
                    // linked token reads as un-cancelled in exactly that case.
                    throw new TimeoutException($"No answer from {endpoint} within {_requestTimeout}");
                }
            }, ctx);
        }
        catch (HttpRequestException ex)
        {
            return WasRefused(ex) ? ProbeVerdict.NotListening : ProbeVerdict.Unanswered;
        }
        catch (TimeoutException)
        {
            // Every attempt hit the bound. Polly rethrows the last one, and an unhandled throw here
            // would take the restart down instead of reporting a verdict.
            return ProbeVerdict.Unanswered;
        }
        catch (OperationCanceledException) when (!ctx.IsCancellationRequested)
        {
            // The attempt bound expired: the port took the connection and said nothing.
            return ProbeVerdict.Unanswered;
        }
    }

    /// <summary>
    ///     True when the connection was refused — the one failure that proves nothing holds the
    ///     port. A reset, a hang-up or a timeout all leave a listener possible.
    /// </summary>
    private static bool WasRefused(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
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
