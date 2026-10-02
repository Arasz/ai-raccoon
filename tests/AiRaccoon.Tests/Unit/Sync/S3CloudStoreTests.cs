using System.Net;
using System.Reflection;
using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sync;

/// <summary>
///     S3CloudStore credential modes: persisted keys (unchanged) and the AWS default
///     credential chain (--cli mode). Chain failures are pinned through an IAmazonS3
///     stub — the real chain resolves lazily outside the HTTP path.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class S3CloudStoreTests
{
    [Fact]
    public void Ctor_ChainMode_BuildsClientWithServiceUrl()
    {
        var options = new SyncOptions
        {
            Endpoint = "http://s3.example.com",
            Bucket = "memories",
            S3Chain = true
        };

        var client = S3CloudStore.CreateClient(options);

        client.ShouldBeOfType<AmazonS3Client>();
        // The SDK normalizes the endpoint Uri, appending the trailing slash.
        ((AmazonS3Client)client).Config.ServiceURL.ShouldBe("http://s3.example.com/");
    }

    [Fact]
    public void CreateClient_EndpointAndRegion_KeepsServiceUrlAndSetsAuthenticationRegion()
    {
        var options = new SyncOptions
        {
            Endpoint = "http://127.0.0.1:32828",
            Bucket = "memories",
            Region = "us-east-1",
            S3Chain = true
        };

        var client = (AmazonS3Client)S3CloudStore.CreateClient(options);

        client.Config.ServiceURL.ShouldBe("http://127.0.0.1:32828/");
        client.Config.AuthenticationRegion.ShouldBe("us-east-1");
    }

    [Fact]
    public void CreateClient_EndpointWithoutRegion_KeepsServiceUrl()
    {
        var options = new SyncOptions
        {
            Endpoint = "http://127.0.0.1:32828",
            Bucket = "memories",
            S3Chain = true
        };

        var client = (AmazonS3Client)S3CloudStore.CreateClient(options);

        client.Config.ServiceURL.ShouldBe("http://127.0.0.1:32828/");
    }

    [Fact]
    public void CreateClient_RegionWithoutEndpoint_SetsRegionEndpoint()
    {
        var options = new SyncOptions
        {
            Bucket = "memories",
            Region = "us-east-1",
            S3Chain = true
        };

        var client = (AmazonS3Client)S3CloudStore.CreateClient(options);

        client.Config.RegionEndpoint.ShouldBe(RegionEndpoint.USEast1);
    }

    [Fact]
    public void Ctor_ChainMode_ConstructsWithoutKeys()
    {
        var options = new SyncOptions
        {
            Endpoint = "http://s3.example.com",
            Bucket = "memories",
            S3Chain = true
        };

        var store = new S3CloudStore(options, NullLogger<S3CloudStore>.Instance);

        store.ShouldNotBeNull();
    }

    [Fact]
    public void Ctor_NeitherMode_ThrowsArgumentException()
    {
        var options = new SyncOptions { Endpoint = "http://s3.example.com", Bucket = "memories" };

        Should.Throw<ArgumentException>(() => new S3CloudStore(options, NullLogger<S3CloudStore>.Instance));
    }

    [Fact]
    public async Task Pull_NoCredentials_ThrowsSyncAuthFailed()
    {
        var store = Store(ThrowingS3(new AmazonClientException("Failed to resolve AWS credentials")));

        await Should.ThrowAsync<SyncAuthFailedException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pull_Forbidden_ThrowsSyncAuthFailed()
    {
        var store = Store(ThrowingS3(new AmazonS3Exception("Access Denied")
        {
            StatusCode = HttpStatusCode.Forbidden
        }));

        await Should.ThrowAsync<SyncAuthFailedException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_HttpRequestException_ThrowsSyncNetwork()
    {
        var store = Store(ThrowingS3(new HttpRequestException("connection reset")));

        await Should.ThrowAsync<SyncNetworkException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pull_MissingObject_ReturnsNull()
    {
        var store = Store(ThrowingS3(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound }));

        var result = await store.PullAsync("bank.db", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Pull_ExistingObject_ReturnsDataAndUnquotedETag()
    {
        var store = Store(RespondingS3(_ => new GetObjectResponse
        {
            ResponseStream = new MemoryStream([.. "snapshot"u8]),
            ETag = "\"abc123\""
        }));

        var result = await store.PullAsync("bank.db", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Data.ShouldBe([.. "snapshot"u8]);
        result.ETag.ShouldBe("abc123");
    }

    [Fact]
    public async Task Pull_ServerError_ThrowsSyncNetwork()
    {
        var store = Store(ThrowingS3(new AmazonS3Exception("boom") { StatusCode = HttpStatusCode.InternalServerError }));

        var ex = await Should.ThrowAsync<SyncNetworkException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("S3 pull failed: boom");
    }

    [Fact]
    public async Task Pull_HttpRequestException_ThrowsSyncNetwork()
    {
        var store = Store(ThrowingS3(new HttpRequestException("connection reset")));

        var ex = await Should.ThrowAsync<SyncNetworkException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("S3 pull failed: connection reset");
    }

    [Fact]
    public async Task Pull_NonS3ServiceException_PropagatesUnmapped()
    {
        var store = Store(ThrowingS3(new AmazonServiceException("boom") { StatusCode = HttpStatusCode.InternalServerError }));

        await Should.ThrowAsync<AmazonServiceException>(() => store.PullAsync("bank.db", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_NonS3ServiceException_PropagatesUnmapped()
    {
        var store = Store(ThrowingS3(new AmazonServiceException("boom") { StatusCode = HttpStatusCode.InternalServerError }));

        await Should.ThrowAsync<AmazonServiceException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_Conflict_ThrowsSyncConflict()
    {
        var store = Store(ThrowingS3(new AmazonS3Exception("stale") { StatusCode = HttpStatusCode.PreconditionFailed }));

        await Should.ThrowAsync<SyncConflictException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], "abc",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_NoCredentials_ThrowsSyncAuthFailed()
    {
        var store = Store(ThrowingS3(new AmazonClientException("Failed to resolve AWS credentials")));

        await Should.ThrowAsync<SyncAuthFailedException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_Forbidden_ThrowsSyncAuthFailed()
    {
        var store = Store(ThrowingS3(new AmazonS3Exception("Access Denied") { StatusCode = HttpStatusCode.Forbidden }));

        await Should.ThrowAsync<SyncAuthFailedException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_ServerError_ThrowsSyncNetwork()
    {
        var store = Store(ThrowingS3(new AmazonS3Exception("boom") { StatusCode = HttpStatusCode.InternalServerError }));

        var ex = await Should.ThrowAsync<SyncNetworkException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], null,
            TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("S3 push failed: boom");
    }

    [Fact]
    public async Task Push_HttpRequestException_MessageNamesPush()
    {
        var store = Store(ThrowingS3(new HttpRequestException("connection reset")));

        var ex = await Should.ThrowAsync<SyncNetworkException>(() => store.PushAsync("bank.db", [.. "snapshot"u8], null,
            TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("S3 push failed: connection reset");
    }

    [Fact]
    public async Task Push_WithETag_SendsQuotedIfMatchAndReturnsUnquotedETag()
    {
        PutObjectRequest? sent = null;
        var store = Store(RespondingS3(request =>
        {
            sent = (PutObjectRequest)request!;
            return new PutObjectResponse { ETag = "\"new456\"" };
        }));

        var newEtag = await store.PushAsync("bank.db", [.. "snapshot"u8], "abc", TestContext.Current.CancellationToken);

        newEtag.ShouldBe("new456");
        sent.ShouldNotBeNull();
        sent.Headers["If-Match"].ShouldBe("\"abc\"");
    }

    [Fact]
    public async Task Push_WithoutETag_SendsNoIfMatch()
    {
        PutObjectRequest? sent = null;
        var store = Store(RespondingS3(request =>
        {
            sent = (PutObjectRequest)request!;
            return new PutObjectResponse { ETag = "\"new456\"" };
        }));

        await store.PushAsync("bank.db", [.. "snapshot"u8], null, TestContext.Current.CancellationToken);

        sent.ShouldNotBeNull();
        sent.Headers.Keys.ShouldNotContain("If-Match");
    }

    [Fact]
    public async Task Push_MissingResponseETag_ReturnsEmpty()
    {
        var store = Store(RespondingS3(_ => new PutObjectResponse()));

        var newEtag = await store.PushAsync("bank.db", [.. "snapshot"u8], null, TestContext.Current.CancellationToken);

        newEtag.ShouldBe("");
    }

    private static S3CloudStore Store(IAmazonS3 s3) => new(s3, "memories", NullLogger<S3CloudStore>.Instance);

    private static IAmazonS3 ThrowingS3(Exception exception) => RespondingS3(_ => throw exception);

    /// <summary>Stubs every IAmazonS3 call: the responder gets the request (or first argument) and returns the response.</summary>
    private static IAmazonS3 RespondingS3(Func<object?, object?> responder)
    {
        var proxy = DispatchProxy.Create<IAmazonS3, S3Proxy>();
        ((S3Proxy)proxy).Responder = responder;
        return proxy;
    }

    private class S3Proxy : DispatchProxy
    {
        public Func<object?, object?>? Responder { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var response = Responder!(args?[0]);
            var payload = targetMethod!.ReturnType.GenericTypeArguments[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(payload).Invoke(null, [response]);
        }
    }
}
