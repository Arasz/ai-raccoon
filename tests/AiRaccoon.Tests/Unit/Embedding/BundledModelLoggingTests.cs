using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     BundledModel download bootstrap logs its progress through a nested LoggerMessage class:
///     downloading (Information), download failure (Error), verified (Debug).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BundledModelLoggingTests
{
    [Fact]
    public async Task EnsureDownloadsAsync_WhenDownloadFails_LogsDownloadingAndFailure()
    {
        var logger = new FakeLogger<BundledModel>();
        var model = new BundledModel(logger, new ThrowingHttpClientFactory());
        var tempRoot = TestData.CreateTempRoot("bundled-model-logging");

        try
        {
            var result = await model.EnsureDownloadsAsync(tempRoot, TestContext.Current.CancellationToken);

            result.AllPresent.ShouldBeFalse();
            result.Errors.ShouldNotBeEmpty();
            var records = logger.Collector.GetSnapshot();
            records.ShouldContain(r => r.Id.Id == 410 && r.Level == LogLevel.Information
                                       && r.Message.Contains("Downloading bundled model asset", StringComparison.Ordinal));
            records.ShouldContain(r => r.Id.Id == 411 && r.Level == LogLevel.Error
                                       && r.Message.Contains("Failed to download bundled model asset", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task EnsureAsync_WhenAssetsVerified_LogsDebug()
    {
        if (!FindBundledModel())
        {
            // The bundled ONNX is gitignored — fresh checkouts (CI) never carry it, and this
            // branch of EnsureAsync cannot run without the real SHA-pinned assets.
            Assert.Skip("bundled ONNX model not present (gitignored; absent on CI)");
        }

        var logger = new FakeLogger<BundledModel>();
        var model = new BundledModel(logger, new ThrowingHttpClientFactory());

        var result = await model.EnsureAsync(TestContext.Current.CancellationToken);

        result.AllPresent.ShouldBeTrue();
        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Id.Id.ShouldBe(412);
        record.Level.ShouldBe(LogLevel.Debug);
        record.Message.ShouldContain("Bundled model assets verified");
    }

    /// <summary>Walks up from the test output to the repo's src/AiRaccoon/Models — the same
    /// resolution EnsureAsync uses — and reports whether the pinned assets are present.</summary>
    private static bool FindBundledModel()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Models", BundledModel.ModelFileName);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler());

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) => throw new HttpRequestException("connection refused");
        }
    }
}
