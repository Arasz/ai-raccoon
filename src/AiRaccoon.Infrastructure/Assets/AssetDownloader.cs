using System.Net;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Assets;

/// <summary>A download that succeeded at the transport level but produced no bytes to verify.</summary>
public sealed class EmptyDownloadException(string message) : Exception(message);

/// <summary>
///     Fetches a SHA-pinned asset over HTTP. A zero-length body fails here, naming the URL and the
///     response, rather than reaching the caller's SHA check as the hash of nothing.
/// </summary>
public sealed class AssetDownloader
{
    private const int DefaultAttempts = 3;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http;
    private readonly int _attempts;
    private readonly TimeSpan _retryDelay;

    public AssetDownloader(HttpClient http, int? attempts = null, TimeSpan? retryDelay = null)
    {
        Guard.IsNotNull(http);
        Guard.IsGreaterThan(attempts ?? DefaultAttempts, 0);

        _http = http;
        _attempts = attempts ?? DefaultAttempts;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    /// <summary>
    ///     Returns the asset bytes, retrying only what a CDN can fix by itself: an empty body, a
    ///     timeout, a throttle or a 5xx. Every other status fails on the first response.
    /// </summary>
    public async Task<byte[]> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(url);

        for (var attempt = 1; ; attempt++)
        {
            var lastAttempt = attempt >= _attempts;
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    return bytes;
                }

                if (lastAttempt)
                {
                    throw new EmptyDownloadException(
                        $"{url} returned 0 bytes on each of {attempt} attempt(s) "
                        + $"(HTTP {(int)response.StatusCode}, Content-Length {ContentLength(response)}); nothing was downloaded to verify.");
                }
            }
            else if (lastAttempt || !IsWorthRetrying(response.StatusCode))
            {
                throw new HttpRequestException(
                    $"{url} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) after {attempt} attempt(s).",
                    null,
                    response.StatusCode);
            }

            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ContentLength(HttpResponseMessage response) =>
        response.Content.Headers.ContentLength?.ToString() ?? "absent";

    private static bool IsWorthRetrying(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
