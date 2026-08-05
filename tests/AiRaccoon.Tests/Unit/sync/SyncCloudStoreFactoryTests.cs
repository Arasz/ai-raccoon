using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Tests.Unit.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.sync;

/// <summary>
///     Per-call cloud-store resolution (findings F13): the store is rebuilt from the
///     CURRENT settings rows on every memory_sync call, so `sync add/remove` take effect
///     without a restart. Secrets come from the settings table, not the environment.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncCloudStoreFactoryTests
{
    private static SyncCloudStoreFactory Factory(FakeConfigStore store) => new(store, NullLoggerFactory.Instance);

    private static void SeedFull(FakeConfigStore store)
    {
        store.Settings[SyncSettingsKeys.Endpoint] = "http://s3.example.com";
        store.Settings[SyncSettingsKeys.Bucket] = "memories";
        store.Settings[SyncSettingsKeys.AccessKey] = "ak";
        store.Settings[SyncSettingsKeys.SecretKey] = "sk";
    }

    [Fact]
    public async Task Create_WithoutSettings_ReturnsNullCloudStore()
    {
        var cloud = await Factory(new FakeConfigStore()).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<NullCloudStore>();
    }

    [Fact]
    public async Task Create_WithFullSettings_ReturnsS3CloudStore()
    {
        var store = new FakeConfigStore();
        SeedFull(store);

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<S3CloudStore>();
    }

    [Fact]
    public async Task Create_WithEndpointBucketButNoSecrets_ReturnsNullCloudStore()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Endpoint] = "http://s3.example.com",
                [SyncSettingsKeys.Bucket] = "memories"
            }
        };

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<NullCloudStore>();
    }

    [Fact]
    public async Task ReadOptionsAsync_MapsAllSettingsRows()
    {
        var store = new FakeConfigStore();
        SeedFull(store);
        store.Settings[SyncSettingsKeys.Region] = "us-east-1";
        store.Settings[SyncSettingsKeys.ObjectKey] = "bank.db";

        var options = await Factory(store).ReadOptionsAsync(TestContext.Current.CancellationToken);

        options.Endpoint.ShouldBe("http://s3.example.com");
        options.Bucket.ShouldBe("memories");
        options.AccessKey.ShouldBe("ak");
        options.SecretKey.ShouldBe("sk");
        options.Region.ShouldBe("us-east-1");
        options.ObjectKey.ShouldBe("bank.db");
        options.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadOptionsAsync_ReflectsLatestSettings_AfterSyncRemove()
    {
        var store = new FakeConfigStore();
        SeedFull(store);
        var factory = Factory(store);

        foreach (var key in new[]
                 {
                     SyncSettingsKeys.Endpoint, SyncSettingsKeys.Bucket, SyncSettingsKeys.AccessKey,
                     SyncSettingsKeys.SecretKey
                 })
        {
            await store.DeleteSettingAsync(key, TestContext.Current.CancellationToken);
        }

        var cloud = await factory.CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<NullCloudStore>();
    }
}
