using System.Net;
using AiRaccoon.Infrastructure.Assets;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Assets;

/// <summary>
///     A zero-byte body must fail at the download naming the URL, and only a failure a CDN can fix
///     by itself may be retried (CI run 31318134938).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class AssetDownloaderTests
{
    private const string Url = "https://huggingface.co/leliuga/all-MiniLM-L6-v2-GGUF/resolve/main/all-MiniLM-L6-v2.Q5_K_M.gguf";

    [Fact]
    public async Task GetAsync_WhenEveryAttemptYieldsAnEmptyBody_ThrowsNamingTheUrlAndWhatItGot()
    {
        var handler = new ScriptedHandler(Empty(), Empty(), Empty());
        using var http = new HttpClient(handler);

        var thrown = await Should.ThrowAsync<EmptyDownloadException>(() => new AssetDownloader(http, retryDelay: TimeSpan.Zero).GetAsync(Url, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(Url);
        thrown.Message.ShouldContain("0 bytes");
        thrown.Message.ShouldContain("HTTP 200");
        handler.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task GetAsync_WhenAnEmptyBodyIsFollowedByTheAsset_ReturnsTheAsset()
    {
        var handler = new ScriptedHandler(Empty(), Body([.. "the model"u8]));
        using var http = new HttpClient(handler);

        var bytes = await new AssetDownloader(http, retryDelay: TimeSpan.Zero).GetAsync(Url, TestContext.Current.CancellationToken);

        bytes.ShouldBe([.. "the model"u8]);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_WhenTheAssetIsGone_FailsWithoutRetrying()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound),
            Body([.. "never reached"u8]));
        using var http = new HttpClient(handler);

        var thrown = await Should.ThrowAsync<HttpRequestException>(() => new AssetDownloader(http, retryDelay: TimeSpan.Zero).GetAsync(Url, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("404");
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_WhenTheCdnReturnsServiceUnavailableThenTheAsset_ReturnsTheAsset()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Body([.. "the model"u8]));
        using var http = new HttpClient(handler);

        var bytes = await new AssetDownloader(http, retryDelay: TimeSpan.Zero).GetAsync(Url, TestContext.Current.CancellationToken);

        bytes.ShouldBe([.. "the model"u8]);
        handler.Calls.ShouldBe(2);
    }

    private static HttpResponseMessage Empty() => Body([]);

    private static HttpResponseMessage Body(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>Replays the given responses in order; the last one repeats.</summary>
    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            return Task.FromResult(response);
        }
    }
}
