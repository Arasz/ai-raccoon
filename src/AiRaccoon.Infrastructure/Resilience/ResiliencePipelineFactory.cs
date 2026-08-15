using System.Net;
using System.Net.Sockets;
using Polly;
using Polly.Retry;

namespace AiRaccoon.Infrastructure.Resilience;

/// <summary>
///     Factory for Polly v8 resilience pipelines configured with exponential backoff
///     and decorrelated random jitter to prevent thundering-herd collisions.
/// </summary>
public static class ResiliencePipelineFactory
{
    /// <summary>
    ///     Pipeline for local loopback probes (ServerProbe, ServerRestart, ObservabilityRunner):
    ///     fast exponential retries with jitter for transient socket failures and startup windows.
    /// </summary>
    public static ResiliencePipeline CreateProbePipeline(
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        Func<OnRetryArguments<object>, ValueTask>? onRetry = null)
    {
        var delay = initialDelay ?? TimeSpan.FromMilliseconds(25);

        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxAttempts - 1,
                Delay = delay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<TimeoutException>(),
                OnRetry = onRetry
            })
            .Build();
    }

    /// <summary>
    ///     Pipeline for HttpResponseMessage operations (probe requests or remote downloads).
    /// </summary>
    public static ResiliencePipeline<HttpResponseMessage> CreateAssetDownloaderPipeline(
        int maxAttempts = 5,
        TimeSpan? initialDelay = null,
        Func<OnRetryArguments<HttpResponseMessage>, ValueTask>? onRetry = null)
    {
        var delay = initialDelay ?? TimeSpan.FromMilliseconds(500);

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = maxAttempts - 1,
                Delay = delay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<TimeoutException>()
                    .Handle<Exception>(ex => ex.GetType().Name == "EmptyDownloadException")
                    .HandleResult(response => IsTransientHttpStatusCode(response.StatusCode)),
                OnRetry = onRetry
            })
            .Build();
    }

    /// <summary>
    ///     Recognizes transient HTTP status codes eligible for automatic retry (5xx, 408, 429).
    /// </summary>
    public static bool IsTransientHttpStatusCode(HttpStatusCode statusCode)
        => (int)statusCode >= 500
           || statusCode == HttpStatusCode.RequestTimeout
           || statusCode == HttpStatusCode.TooManyRequests;
}
