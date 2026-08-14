using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiRaccoon.Resilience;
using CommunityToolkit.Diagnostics;
using Polly;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     Recognizes an ai-raccoon MCP server on an endpoint: POST /mcp with an MCP Accept header and a
///     non-JSON body; recognized iff status ∈ {400,401,405,406} and the body mentions jsonrpc.
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

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = new StringContent("x", Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");

                using var response = await _httpClientFactory.CreateClient(nameof(ServerProbe)).SendAsync(request, attemptCts.Token);

                if (ResiliencePipelineFactory.IsTransientHttpStatusCode(response.StatusCode))
                {
                    throw new HttpRequestException($"Transient HTTP status {response.StatusCode} from {endpoint}");
                }

                var body = await response.Content.ReadAsStringAsync(attemptCts.Token);
                return body.Contains("jsonrpc", StringComparison.Ordinal) ? ProbeVerdict.Answered : ProbeVerdict.NotListening;
            }, ctx);
        }
        catch (HttpRequestException)
        {
            return ProbeVerdict.NotListening;
        }
        catch (OperationCanceledException) when (!ctx.IsCancellationRequested)
        {
            return ProbeVerdict.NotListening;
        }
    }

    public static Uri EndpointFor(int port) => new($"http://127.0.0.1:{port}/mcp");

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
