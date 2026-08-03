using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Provisioning;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ExtensionProvisionerTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task EnsureProvisioned_WithCloudSync_DownloadsSyncModule()
    {
        var archives = OsxArm64Archives();
        var handler = new FakeHandler(uri => archives[AssetName(uri)]);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, asset => Hash(archives[asset]),
            true);

        var result = await provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        result.CloudSync.ShouldBe(ModulePath("cloudsync.dylib"));
        File.Exists(result.CloudSync).ShouldBeTrue();
        handler.Requested.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EnsureProvisioned_RequestsPinnedGithubAssetUrl()
    {
        var archives = OsxArm64Archives();
        var handler = new FakeHandler(uri => archives[AssetName(uri)]);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, asset => Hash(archives[asset]),
            true);

        await provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        handler.Requested.Select(u => u.AbsoluteUri).ShouldContain(
            "https://github.com/sqliteai/sqlite-sync/releases/download/1.1.2/cloudsync-macos-arm64-1.1.2.tar.gz");
    }

    [Fact]
    public async Task EnsureProvisioned_WithoutCloudSync_DownloadsNothing()
    {
        var archives = OsxArm64Archives();
        var handler = new FakeHandler(uri => archives[AssetName(uri)]);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, asset => Hash(archives[asset]));

        var result = await provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        result.CloudSync.ShouldBeNull();
        handler.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureProvisioned_WhenModuleAlreadyPresent_SkipsDownload()
    {
        Directory.CreateDirectory(Path.Combine(_dataRoot, "extensions", "osx-arm64"));
        File.WriteAllText(ModulePath("cloudsync.dylib"), "x");
        var handler = new FakeHandler(_ => []);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, _ => Hash([]), true);

        await provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        handler.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureProvisioned_OnChecksumMismatch_ThrowsAndLeavesNoFile()
    {
        var archive = TarGz(("cloudsync.dylib", [1]));
        var handler = new FakeHandler(_ => archive);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, _ => new string('0', 64), true);

        var exception = await Should.ThrowAsync<ExtensionProvisioningException>(() =>
            provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Checksum mismatch");
        Directory.Exists(provisioner.ExtensionDirectory).ShouldBeTrue();
        Directory.EnumerateFiles(provisioner.ExtensionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureProvisioned_WithoutManifestChecksum_Throws()
    {
        var handler = new FakeHandler(_ => []);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, _ => null, true);

        var exception = await Should.ThrowAsync<ExtensionProvisioningException>(() =>
            provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("No checksum recorded");
        handler.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureProvisioned_ForMuslRid_ThrowsListingMissingBinaries()
    {
        var provisioner = new ExtensionProvisioner(_dataRoot, "linux-musl-x64", new HttpClient(), _ => null,
            true);

        var exception = await Should.ThrowAsync<ExtensionProvisioningException>(() =>
            provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("cloudsync-linux-musl-x86_64-1.1.2.tar.gz");
    }

    [Fact]
    public async Task EnsureProvisioned_ForRidWithoutAssets_Throws()
    {
        var provisioner = new ExtensionProvisioner(_dataRoot, "win-arm64", new HttpClient(), _ => null);

        var exception = await Should.ThrowAsync<ExtensionProvisioningException>(() =>
            provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("win-arm64");
    }

    private static Dictionary<string, byte[]> OsxArm64Archives() =>
        new()
        {
            ["cloudsync-macos-arm64-1.1.2.tar.gz"] = TarGz(("cloudsync.dylib", [3]))
        };

    private static string AssetName(Uri uri) => Path.GetFileName(uri.AbsolutePath);

    private string ModulePath(string fileName) => Path.Combine(_dataRoot, "extensions", "osx-arm64", fileName);

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-provision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] TarGz(params (string Name, byte[] Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, true))
        {
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, true))
            {
                foreach (var (name, content) in files)
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                    {
                        DataStream = new MemoryStream(content)
                    });
                }
            }
        }

        return buffer.ToArray();
    }

    private sealed class FakeHandler(Func<Uri, byte[]> bodyFor) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bodyFor(request.RequestUri!))
            };
            return Task.FromResult(response);
        }
    }
}
