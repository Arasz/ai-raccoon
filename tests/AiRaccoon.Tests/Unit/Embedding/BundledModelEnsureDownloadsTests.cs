using System.Net;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>Network-free coverage of the bundled-asset download path (see docs/work/features-native-memory/native-memory.feature).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BundledModelEnsureDownloadsTests : IDisposable
{
    /// <summary>SHA-256 of zero bytes — what hashing a failed download produces.</summary>
    private const string EmptyStringSha256 = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

    private readonly string _root = TestData.CreateTempRoot("airaccoon-embedding-download-tests");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is scanned periodically anyway.
        }
    }

    [Fact]
    public async Task EnsureDownloadsAsync_WithFailingHttp_ReturnsErrorsWithoutThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);

        var result = await new BundledModel(NullLogger<BundledModel>.Instance, new StubHttpClientFactory(http)).EnsureDownloadsAsync(DownloadDir(), TestContext.Current.CancellationToken);

        result.AllPresent.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task EnsureDownloadsAsync_WithWrongSha_ReportsMismatch()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([.. "not the model"u8])
        });
        using var http = new HttpClient(handler);

        var result = await new BundledModel(NullLogger<BundledModel>.Instance, new StubHttpClientFactory(http)).EnsureDownloadsAsync(DownloadDir(), TestContext.Current.CancellationToken);

        result.AllPresent.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("sha256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnsureDownloadsAsync_WithAnEmptyBody_ReportsTheEmptyDownload_NotTheHashOfNothing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        });
        using var http = new HttpClient(handler);

        var result = await new BundledModel(NullLogger<BundledModel>.Instance, new StubHttpClientFactory(http)).EnsureDownloadsAsync(DownloadDir(), TestContext.Current.CancellationToken);

        result.AllPresent.ShouldBeFalse();
        result.Errors.ShouldNotContain(
            e => e.Contains(EmptyStringSha256, StringComparison.OrdinalIgnoreCase),
            "a zero-byte body must be reported where it happened, not as the sha256 of nothing");
        result.Errors.ShouldContain(e => e.Contains("0 bytes", StringComparison.Ordinal));
        result.Errors.ShouldContain(e => e.Contains("huggingface.co", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureDownloadsAsync_WhenCancelled_ReturnsErrorNotThrow()
    {
        var handler = new StuckHandler();
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await new BundledModel(NullLogger<BundledModel>.Instance, new StubHttpClientFactory(http)).EnsureDownloadsAsync(DownloadDir(), cts.Token);

        result.AllPresent.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void BundledModel_MissingModelMessage_RecommendsModelSetLocal()
    {
        var message = BundledModel.MissingBundledModelMessage(BundledModel.ModelFileName);

        message.ShouldContain(BundledModel.ModelFileName);
        message.ShouldContain("model set local");
        message.ShouldContain("path-to-onnx");
    }

    private string DownloadDir()
    {
        var dir = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class StubHttpClientFactory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private sealed class StuckHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }
}
