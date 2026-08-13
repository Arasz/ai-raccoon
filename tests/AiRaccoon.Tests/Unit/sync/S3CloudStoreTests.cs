using System.Net;
using System.Reflection;
using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.sync;

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
    public void Ctor_ChainMode_ConstructsWithoutKeys()
    {
        var options = new SyncOptions
        {
            Endpoint = "http://s3.example.com",
            Bucket = "memories",
            S3Chain = true
        };

        var store = new S3CloudStore(options);

        store.ShouldNotBeNull();
    }

    [Fact]
    public void Ctor_NeitherMode_ThrowsArgumentException()
    {
        var options = new SyncOptions { Endpoint = "http://s3.example.com", Bucket = "memories" };

        Should.Throw<ArgumentException>(() => new S3CloudStore(options));
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

    private static S3CloudStore Store(IAmazonS3 s3) => new(s3, "memories", NullLogger<S3CloudStore>.Instance);

    private static IAmazonS3 ThrowingS3(Exception exception)
    {
        var proxy = DispatchProxy.Create<IAmazonS3, ThrowingS3Proxy>();
        ((ThrowingS3Proxy)proxy).Exception = exception;
        return proxy;
    }

    private class ThrowingS3Proxy : DispatchProxy
    {
        public Exception? Exception { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw Exception ?? new InvalidOperationException("no exception configured");
    }
}
