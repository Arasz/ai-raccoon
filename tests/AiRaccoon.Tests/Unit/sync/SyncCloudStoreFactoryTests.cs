using AiRaccoon.Infrastructure.Options;
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
    public async Task Create_WithAzureSettings_ReturnsAzureBlobCloudStore()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Provider] = "azure",
                [SyncSettingsKeys.ConnectionString] = FakeConnectionString,
                [SyncSettingsKeys.Container] = "memories",
                [SyncSettingsKeys.ObjectKey] = "bank.db"
            }
        };

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<AzureBlobCloudStore>();
    }

    [Fact]
    public async Task Create_WithAzureMissingConnectionString_ReturnsNullCloudStore()
    {
        var store = new FakeConfigStore
        {
            Settings = { [SyncSettingsKeys.Provider] = "azure", [SyncSettingsKeys.Container] = "memories" }
        };

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<NullCloudStore>();
    }

    [Fact]
    public async Task Create_WithProviderAzureAndS3RowsOnly_ReturnsNullCloudStore()
    {
        // The silently-dead trap: provider says azure but only s3 rows exist.
        var store = new FakeConfigStore { Settings = { [SyncSettingsKeys.Provider] = "azure" } };
        SeedFull(store);

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<NullCloudStore>();
    }

    [Fact]
    public async Task Create_WithProviderS3AndFullS3Settings_ReturnsS3CloudStore()
    {
        var store = new FakeConfigStore { Settings = { [SyncSettingsKeys.Provider] = "s3" } };
        SeedFull(store);

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<S3CloudStore>();
    }

    [Fact]
    public async Task Create_WithProviderRowOnly_ReturnsNullCloudStore()
    {
        var store = new FakeConfigStore { Settings = { [SyncSettingsKeys.Provider] = "azure" } };

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<NullCloudStore>();
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

    // Azurite's published dev account key — valid base64, obviously fake.
    private const string FakeConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;EndpointSuffix=core.windows.net";

    [Fact]
    public async Task ReadOptionsAsync_MapsAzureRows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Provider] = "azure",
                [SyncSettingsKeys.ConnectionString] = FakeConnectionString,
                [SyncSettingsKeys.Container] = "memories",
                [SyncSettingsKeys.ObjectKey] = "bank.db"
            }
        };

        var options = await Factory(store).ReadOptionsAsync(TestContext.Current.CancellationToken);

        options.Provider.ShouldBe(SyncProvider.Azure);
        options.ConnectionString.ShouldBe(FakeConnectionString);
        options.Container.ShouldBe("memories");
        options.ObjectKey.ShouldBe("bank.db");
        options.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadOptionsAsync_ProviderDefaultsToS3_WhenRowAbsent()
    {
        var store = new FakeConfigStore();
        SeedFull(store);

        var options = await Factory(store).ReadOptionsAsync(TestContext.Current.CancellationToken);

        options.Provider.ShouldBe(SyncProvider.S3);
    }

    [Fact]
    public async Task ReadOptionsAsync_UnknownProviderValue_DefaultsToS3()
    {
        var store = new FakeConfigStore { Settings = { [SyncSettingsKeys.Provider] = "minio" } };

        var options = await Factory(store).ReadOptionsAsync(TestContext.Current.CancellationToken);

        options.Provider.ShouldBe(SyncProvider.S3);
    }

    [Fact]
    public async Task ReadOptionsAsync_MapsAzureAccountRow()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Provider] = "azure",
                [SyncSettingsKeys.AzureAccount] = "myacct",
                [SyncSettingsKeys.Container] = "memories"
            }
        };

        var options = await Factory(store).ReadOptionsAsync(TestContext.Current.CancellationToken);

        options.Account.ShouldBe("myacct");
        options.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadOptionsAsync_MapsS3ChainRow()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Endpoint] = "http://s3.example.com",
                [SyncSettingsKeys.Bucket] = "memories",
                [SyncSettingsKeys.S3Chain] = "true"
            }
        };

        var options = await Factory(store).ReadOptionsAsync(TestContext.Current.CancellationToken);

        options.S3Chain.ShouldBeTrue();
        options.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task Create_WithAzureAccountMode_ReturnsAzureBlobCloudStore()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Provider] = "azure",
                [SyncSettingsKeys.AzureAccount] = "myacct",
                [SyncSettingsKeys.Container] = "memories"
            }
        };

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<AzureBlobCloudStore>();
    }

    [Fact]
    public async Task IsConfigured_AzureAccountMode_True()
    {
        var options = new SyncOptions
        {
            Provider = SyncProvider.Azure,
            Account = "myacct",
            Container = "memories"
        };

        options.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task Create_WithS3ChainMode_ReturnsS3CloudStore()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Endpoint] = "http://s3.example.com",
                [SyncSettingsKeys.Bucket] = "memories",
                [SyncSettingsKeys.S3Chain] = "true"
            }
        };

        var cloud = await Factory(store).CreateAsync(TestContext.Current.CancellationToken);

        cloud.ShouldBeOfType<S3CloudStore>();
    }

    [Fact]
    public async Task IsConfigured_S3ChainMode_True()
    {
        var options = new SyncOptions
        {
            Provider = SyncProvider.S3,
            Endpoint = "http://s3.example.com",
            Bucket = "memories",
            S3Chain = true
        };

        options.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task IsConfigured_MissingBothAzureModes_False()
    {
        var options = new SyncOptions
        {
            Provider = SyncProvider.Azure,
            Container = "memories"
        };

        options.IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public async Task IsConfigured_MissingBothS3Modes_False()
    {
        var options = new SyncOptions
        {
            Provider = SyncProvider.S3,
            Endpoint = "http://s3.example.com",
            Bucket = "memories"
        };

        options.IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public async Task IsConfigured_S3ChainRowParsedCaseInsensitively()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Endpoint] = "http://s3.example.com",
                [SyncSettingsKeys.Bucket] = "memories",
                [SyncSettingsKeys.S3Chain] = "TRUE"
            }
        };

        var options = await Factory(store).ReadOptionsAsync(TestContext.Current.CancellationToken);

        options.S3Chain.ShouldBeTrue();
        options.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadOptionsAsync_AzureIsConfiguredOnlyWhenConnectionStringAndContainerPresent()
    {
        var full = new FakeConfigStore
        {
            Settings =
            {
                [SyncSettingsKeys.Provider] = "azure",
                [SyncSettingsKeys.ConnectionString] = FakeConnectionString,
                [SyncSettingsKeys.Container] = "memories"
            }
        };
        var fullOptions = await Factory(full).ReadOptionsAsync(TestContext.Current.CancellationToken);
        fullOptions.IsConfigured.ShouldBeTrue();

        var missingConnectionString = new FakeConfigStore
        {
            Settings = { [SyncSettingsKeys.Provider] = "azure", [SyncSettingsKeys.Container] = "memories" }
        };
        var noConnectionString = await Factory(missingConnectionString)
            .ReadOptionsAsync(TestContext.Current.CancellationToken);
        noConnectionString.IsConfigured.ShouldBeFalse();

        var missingContainer = new FakeConfigStore
        {
            Settings = { [SyncSettingsKeys.Provider] = "azure", [SyncSettingsKeys.ConnectionString] = FakeConnectionString }
        };
        var noContainer = await Factory(missingContainer).ReadOptionsAsync(TestContext.Current.CancellationToken);
        noContainer.IsConfigured.ShouldBeFalse();
    }
}
