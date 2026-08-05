using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using Shouldly;
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
}
