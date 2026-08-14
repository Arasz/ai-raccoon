using System.Net;
using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sync;

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

        Should.Throw<SyncNotConfiguredException>(() => new AzureBlobCloudStore(options, NullLogger<AzureBlobCloudStore>.Instance));
    }

    [Fact]
    public void Ctor_MissingContainer_ThrowsArgumentException()
    {
        var options = new SyncOptions { ConnectionString = FakeConnectionString };

        Should.Throw<ArgumentException>(() => new AzureBlobCloudStore(options, NullLogger<AzureBlobCloudStore>.Instance));
    }

    [Fact]
    public void Ctor_NeitherMode_ThrowsArgumentException()
    {
        var options = new SyncOptions { Container = "memories" };

        Should.Throw<ArgumentException>(() => new AzureBlobCloudStore(options, NullLogger<AzureBlobCloudStore>.Instance));
    }

    [Fact]
    public void Ctor_AccountMode_InvalidAccountName_ThrowsSyncNotConfigured()
    {
        var options = new SyncOptions { Account = "bad name!", Container = "memories" };

        Should.Throw<SyncNotConfiguredException>(() => new AzureBlobCloudStore(options, NullLogger<AzureBlobCloudStore>.Instance));
    }

    [Fact]
    public void Ctor_AccountMode_BuildsClientWithAccountUri()
    {
        var options = new SyncOptions { Account = "myacct", Container = "memories" };

        var client = AzureBlobCloudStore.CreateClient(options);

        client.Uri.ShouldBe(new Uri("https://myacct.blob.core.windows.net/"));
    }

    [Fact]
    public void Ctor_BothModes_ConnectionStringWins()
    {
        var options = new SyncOptions
        {
            Account = "myacct",
            ConnectionString = FakeConnectionString,
            Container = "memories"
        };

        var client = AzureBlobCloudStore.CreateClient(options);

        client.Uri.ShouldBe(new Uri("https://devstoreaccount1.blob.core.windows.net/"));
    }

    [Fact]
    public async Task Pull_NoAzureLogin_ThrowsSyncAuthFailed()
    {
        var store = Store(new ThrowingBlobHandler(new CredentialUnavailableException("no az login state")));

        await Should.ThrowAsync<SyncAuthFailedException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pull_Unauthorized_ThrowsSyncAuthFailed()
    {
        var store = Store(new CannedBlobHandler(_ => Error(401)));

        await Should.ThrowAsync<SyncAuthFailedException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pull_Forbidden_ThrowsSyncAuthFailed()
    {
        var store = Store(new CannedBlobHandler(_ => Error(403)));

        await Should.ThrowAsync<SyncAuthFailedException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pull_HttpRequestException_ThrowsSyncNetwork()
    {
        var store = Store(new ThrowingBlobHandler(new HttpRequestException("connection reset")));

        await Should.ThrowAsync<SyncNetworkException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_Unauthorized_ThrowsSyncAuthFailed()
    {
        var store = Store(new CannedBlobHandler(_ => Error(401)));

        await Should.ThrowAsync<SyncAuthFailedException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pull_ExistingBlob_ReturnsDataAndUnquotedETag()
    {
        var handler = new CannedBlobHandler(_ => Ok([.. "snapshot"u8], "0x8Dabc"));
        var store = Store(handler);

        var result = await store.PullAsync("bank.db", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Data.ShouldBe([.. "snapshot"u8]);
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

        await Should.ThrowAsync<SyncNetworkException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_WithETag_SendsQuotedIfMatchHeader()
    {
        var handler = new CannedBlobHandler(_ => Created("0x8Dnew"));
        var store = Store(handler);

        var newEtag = await store.PushAsync("bank.db", [.. "snapshot"u8], "0x8Dabc",
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

        await store.PushAsync("bank.db", [.. "snapshot"u8], null, TestContext.Current.CancellationToken);

        handler.Requests.ShouldHaveSingleItem().IfMatch.ShouldBeNull();
    }

    [Fact]
    public async Task Push_Conflict_ThrowsSyncConflictException()
    {
        var store = Store(new CannedBlobHandler(_ => Error(412)));

        await Should.ThrowAsync<SyncConflictException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], "0x8Dabc",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_ServerError_ThrowsSyncNetworkException()
    {
        var store = Store(new CannedBlobHandler(_ => Error(500)));

        await Should.ThrowAsync<SyncNetworkException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], "0x8Dabc",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_ReturnsUnquotedETag()
    {
        var store = Store(new CannedBlobHandler(_ => Created("0x8Dnew")));

        var newEtag = await store.PushAsync("bank.db", [.. "snapshot"u8], null,
            TestContext.Current.CancellationToken);

        newEtag.ShouldBe("0x8Dnew");
    }

    [Fact]
    public async Task Push_WithETagOnMissingBlob_ThrowsSyncConflictException()
    {
        // If-Match against a nonexistent blob also 412s (Put Blob semantics, matches S3).
        var store = Store(new CannedBlobHandler(_ => Error(412)));

        await Should.ThrowAsync<SyncConflictException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], "0x8Dabc",
            TestContext.Current.CancellationToken));
    }

    private static AzureBlobCloudStore Store(HttpMessageHandler handler) =>
        new(
            new BlobServiceClient(FakeConnectionString, new BlobClientOptions
            {
                Retry = { MaxRetries = 0 },
                Transport = new HttpClientTransport(handler)
            }),
            "memories",
            NullLogger<AzureBlobCloudStore>.Instance);

    private static CannedResponse Ok(byte[] body, string etag) =>
        new(
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

    private static CannedResponse Created(string etag) =>
        new(
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

    private static CannedResponse Error(int status) =>
        new(
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

    private sealed class ThrowingBlobHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw exception;
    }

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
