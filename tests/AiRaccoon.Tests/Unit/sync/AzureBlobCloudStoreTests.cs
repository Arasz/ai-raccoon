using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Net;
using Xunit;

namespace AiRaccoon.Tests.Unit.sync;

/// <summary>
///     AzureBlobCloudStore against a canned HTTP transport — no network, no Azurite.
///     The Azure SDK's HttpClientTransport is replaced at the pipeline level.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class AzureBlobCloudStoreTests
{
    // Azurite's published dev account key — valid base64, obviously fake.
    private const string FakeConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;EndpointSuffix=core.windows.net";

    [Fact]
    public void Ctor_InvalidConnectionString_ThrowsSyncNotConfigured()
    {
        var options = new SyncOptions { ConnectionString = "not a connection string", Container = "memories" };

        Should.Throw<SyncNotConfiguredException>(() => new AzureBlobCloudStore(options));
    }

    [Fact]
    public void Ctor_MissingContainer_ThrowsArgumentException()
    {
        var options = new SyncOptions { ConnectionString = FakeConnectionString };

        Should.Throw<ArgumentException>(() => new AzureBlobCloudStore(options));
    }

    [Fact]
    public async Task Pull_ExistingBlob_ReturnsDataAndUnquotedETag()
    {
        var handler = new CannedBlobHandler(_ => Ok("snapshot"u8.ToArray(), "0x8Dabc"));
        var store = Store(handler);

        var result = await store.PullAsync("bank.db", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Data.ShouldBe("snapshot"u8.ToArray());
        result.ETag.ShouldBe("0x8Dabc");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.Path.ShouldBe("/memories/bank.db");
    }

    [Fact]
    public async Task Pull_MissingBlob_ReturnsNull()
    {
        var store = Store(new CannedBlobHandler(_ => Error(404)));

        var result = await store.PullAsync("bank.db", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Pull_ServerError_ThrowsSyncNetworkException()
    {
        var store = Store(new CannedBlobHandler(_ => Error(500)));

        await Should.ThrowAsync<SyncNetworkException>(
            () => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_WithETag_SendsQuotedIfMatchHeader()
    {
        var handler = new CannedBlobHandler(_ => Created("0x8Dnew"));
        var store = Store(handler);

        var newEtag = await store.PushAsync("bank.db", "snapshot"u8.ToArray(), "0x8Dabc",
            TestContext.Current.CancellationToken);

        newEtag.ShouldBe("0x8Dnew");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Put);
        request.Path.ShouldBe("/memories/bank.db");
        request.IfMatch.ShouldBe("\"0x8Dabc\"");
    }

    [Fact]
    public async Task Push_WithoutETag_SendsNoIfMatchHeader()
    {
        var handler = new CannedBlobHandler(_ => Created("0x8Dnew"));
        var store = Store(handler);

        await store.PushAsync("bank.db", "snapshot"u8.ToArray(), null, TestContext.Current.CancellationToken);

        handler.Requests.ShouldHaveSingleItem().IfMatch.ShouldBeNull();
    }

    [Fact]
    public async Task Push_Conflict_ThrowsSyncConflictException()
    {
        var store = Store(new CannedBlobHandler(_ => Error(412)));

        await Should.ThrowAsync<SyncConflictException>(
            () => store.PushAsync("bank.db", "snapshot"u8.ToArray(), "0x8Dabc",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_ServerError_ThrowsSyncNetworkException()
    {
        var store = Store(new CannedBlobHandler(_ => Error(500)));

        await Should.ThrowAsync<SyncNetworkException>(
            () => store.PushAsync("bank.db", "snapshot"u8.ToArray(), "0x8Dabc",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_ReturnsUnquotedETag()
    {
        var store = Store(new CannedBlobHandler(_ => Created("0x8Dnew")));

        var newEtag = await store.PushAsync("bank.db", "snapshot"u8.ToArray(), null,
            TestContext.Current.CancellationToken);

        newEtag.ShouldBe("0x8Dnew");
    }

    [Fact]
    public async Task Push_WithETagOnMissingBlob_ThrowsSyncConflictException()
    {
        // If-Match against a nonexistent blob also 412s (Put Blob semantics, matches S3).
        var store = Store(new CannedBlobHandler(_ => Error(412)));

        await Should.ThrowAsync<SyncConflictException>(
            () => store.PushAsync("bank.db", "snapshot"u8.ToArray(), "0x8Dabc",
                TestContext.Current.CancellationToken));
    }

    private static AzureBlobCloudStore Store(CannedBlobHandler handler) => new(
        new BlobServiceClient(FakeConnectionString, new BlobClientOptions
        {
            Retry = { MaxRetries = 0 },
            Transport = new HttpClientTransport(handler)
        }),
        "memories",
        NullLogger<AzureBlobCloudStore>.Instance);

    private static CannedResponse Ok(byte[] body, string etag) => new(
        200,
        new Dictionary<string, string>
        {
            ["ETag"] = $"\"{etag}\"",
            ["Content-Type"] = "application/octet-stream",
            ["Last-Modified"] = "Wed, 05 Aug 2026 12:00:00 GMT",
            ["x-ms-request-id"] = "00000000-0000-0000-0000-000000000000",
            ["x-ms-version"] = "2025-07-06",
            ["Date"] = "Wed, 05 Aug 2026 12:00:00 GMT",
            ["x-ms-blob-type"] = "BlockBlob",
            ["x-ms-lease-status"] = "unlocked",
            ["x-ms-lease-state"] = "available",
            ["x-ms-server-encrypted"] = "true"
        },
        body);

    private static CannedResponse Created(string etag) => new(
        201,
        new Dictionary<string, string>
        {
            ["ETag"] = $"\"{etag}\"",
            ["Last-Modified"] = "Wed, 05 Aug 2026 12:00:00 GMT",
            ["x-ms-request-id"] = "00000000-0000-0000-0000-000000000000",
            ["x-ms-version"] = "2025-07-06",
            ["Date"] = "Wed, 05 Aug 2026 12:00:00 GMT"
        },
        []);

    private static CannedResponse Error(int status) => new(
        status,
        new Dictionary<string, string>
        {
            ["x-ms-request-id"] = "00000000-0000-0000-0000-000000000000",
            ["x-ms-version"] = "2025-07-06",
            ["Date"] = "Wed, 05 Aug 2026 12:00:00 GMT"
        },
        []);

    private sealed record CannedResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? IfMatch);

    private sealed class CannedBlobHandler(Func<HttpRequestMessage, CannedResponse> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var canned = responder(request);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.IfMatch.FirstOrDefault()?.ToString()));

            var response = new HttpResponseMessage((HttpStatusCode)canned.Status)
            {
                Content = new ByteArrayContent(canned.Body)
            };
            foreach (var (name, value) in canned.Headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }
}
