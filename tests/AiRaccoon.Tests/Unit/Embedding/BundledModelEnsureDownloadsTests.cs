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
        TestData.DeleteTempRoot(_root);
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
    public async Task EnsureDownloadsAsync_FetchesEveryManifestPinnedFile_FromThePinnedRevision()
    {
        var requested = new List<string>();
        var handler = new StubHandler(request =>
        {
            requested.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(handler);
        var target = DownloadDir();
        var bundled = Path.Combine(target, BundledModel.DirectoryName);
        Directory.CreateDirectory(bundled);
        File.Copy(Path.Combine(BundledModel.ResolveDirectory(), "ai-raccoon.manifest.json"), Path.Combine(bundled, "ai-raccoon.manifest.json"));

        var result = await new BundledModel(NullLogger<BundledModel>.Instance, new StubHttpClientFactory(http))
            .EnsureDownloadsAsync(target, TestContext.Current.CancellationToken);

        result.AllPresent.ShouldBeFalse();
        requested.ShouldContain(u => u.EndsWith("/resolve/1dc7835ba0cb9c76a3618d0bf0c427c97671b3c8/onnx/model_fp16.onnx_data", StringComparison.Ordinal));
        requested.ShouldContain(u => u.EndsWith("/resolve/1dc7835ba0cb9c76a3618d0bf0c427c97671b3c8/tokenizer.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureDownloadsAsync_WithoutTheCommittedManifest_ReportsIt_InsteadOfGuessingFiles()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var result = await new BundledModel(NullLogger<BundledModel>.Instance, new StubHttpClientFactory(http))
            .EnsureDownloadsAsync(DownloadDir(), TestContext.Current.CancellationToken);

        result.AllPresent.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("ai-raccoon.manifest.json", StringComparison.Ordinal) && e.Contains("committed", StringComparison.Ordinal));
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
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
