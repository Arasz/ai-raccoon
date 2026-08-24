using AiRaccoon.Infrastructure.Resilience;
using CommunityToolkit.Diagnostics;
using Polly;

namespace AiRaccoon.Infrastructure.Assets;

/// <summary>A download that succeeded at the transport level but produced no bytes to verify.</summary>
public sealed class EmptyDownloadException(string message) : Exception(message);

/// <summary>
///     Fetches a SHA-pinned asset over HTTP with Polly v8 exponential backoff and decorrelated jitter.
///     A zero-length body fails here, naming the URL and the response, rather than reaching the caller's SHA check.
/// </summary>
public sealed class AssetDownloader
{
    private const int DefaultAttempts = 3;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public AssetDownloader(HttpClient http, int? attempts = null, TimeSpan? retryDelay = null, ResiliencePipeline<HttpResponseMessage>? pipeline = null)
    {
        Guard.IsNotNull(http);
        var maxAttempts = attempts ?? DefaultAttempts;
        Guard.IsGreaterThan(maxAttempts, 0);

        _http = http;
        _pipeline = pipeline ?? ResiliencePipelineFactory.CreateAssetDownloaderPipeline(maxAttempts, retryDelay ?? DefaultRetryDelay);
    }

    /// <summary>
    ///     Returns the asset bytes, retrying via Polly exponential backoff with jitter on transient failures
    ///     (CDN empty body, timeout, rate limit 429, or 5xx). Every other status fails on first response.
    /// </summary>
    public async Task<byte[]> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(url);

        var response = await _pipeline.ExecuteAsync(async ct =>
        {
            var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                var contentLen = res.Content.Headers.ContentLength;
                if (contentLen == 0)
                {
                    // Trigger retry on empty body from CDN glitch
                    throw new EmptyDownloadException($"{url} returned 0 bytes (HTTP {(int)res.StatusCode}); retrying...");
                }
            }

            return res;
        }, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > 0)
            {
                return bytes;
            }

            throw new EmptyDownloadException(
                $"{url} returned 0 bytes (HTTP {(int)response.StatusCode}, Content-Length {ContentLength(response)}); nothing was downloaded to verify.");
        }

        throw new HttpRequestException(
            $"{url} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
            null,
            response.StatusCode);
    }

    private static string ContentLength(HttpResponseMessage response) => response.Content.Headers.ContentLength?.ToString() ?? "absent";
}
