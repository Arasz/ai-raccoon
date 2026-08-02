using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AiRaccon.Infrastructure.Provisioning;
using Shouldly;
using Xunit;

namespace AiRaccon.Tests.Store;

public sealed class ExtensionProvisionerTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    [Fact]
    public async Task EnsureProvisioned_DownloadsMissingModules_AndReturnsPaths()
    {
        var archives = OsxArm64Archives();
        var handler = new FakeHandler(uri => archives[AssetName(uri)]);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, asset => Hash(archives[asset]), includeCloudSync: true);

        var result = await provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        result.Vector.ShouldBe(ModulePath("vector0.dylib"));
        result.Memory.ShouldBe(ModulePath("memory0.dylib"));
        result.CloudSync.ShouldBe(ModulePath("sync0.dylib"));
        File.Exists(result.Vector).ShouldBeTrue();
        File.Exists(result.Memory).ShouldBeTrue();
        File.Exists(result.CloudSync).ShouldBeTrue();
        handler.Requested.Count.ShouldBe(3);
    }

    [Fact]
    public async Task EnsureProvisioned_RequestsPinnedGithubAssetUrls()
    {
        var archives = OsxArm64Archives();
        var handler = new FakeHandler(uri => archives[AssetName(uri)]);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, asset => Hash(archives[asset]));

        await provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        handler.Requested.Select(u => u.AbsoluteUri).ShouldContain(
            "https://github.com/sqliteai/sqlite-vector/releases/download/1.0.0/vector-macos-arm64-1.0.0.tar.gz");
        handler.Requested.Select(u => u.AbsoluteUri).ShouldContain(
            "https://github.com/sqliteai/sqlite-memory/releases/download/1.3.5/memory-macos-arm64-full-1.3.5.tar.gz");
    }

    [Fact]
    public async Task EnsureProvisioned_WithoutCloudSync_SkipsSyncDownload()
    {
        var archives = OsxArm64Archives();
        var handler = new FakeHandler(uri => archives[AssetName(uri)]);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, asset => Hash(archives[asset]));

        var result = await provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        result.CloudSync.ShouldBeNull();
        handler.Requested.Count.ShouldBe(2);
        handler.Requested.ShouldNotContain(u => u.AbsoluteUri.Contains("cloudsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureProvisioned_WhenModulesAlreadyPresent_SkipsDownload()
    {
        Directory.CreateDirectory(Path.Combine(_dataRoot, "extensions", "osx-arm64"));
        File.WriteAllText(ModulePath("vector0.dylib"), "x");
        File.WriteAllText(ModulePath("memory0.dylib"), "x");
        var handler = new FakeHandler(_ => Array.Empty<byte>());
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, _ => Hash(Array.Empty<byte>()));

        await provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        handler.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureProvisioned_OnChecksumMismatch_ThrowsAndLeavesNoFile()
    {
        var archive = TarGz(("vector0.dylib", new byte[] { 1 }));
        var handler = new FakeHandler(_ => archive);
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, _ => new string('0', 64));

        var exception = await Should.ThrowAsync<ExtensionProvisioningException>(
            () => provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Checksum mismatch");
        Directory.Exists(provisioner.ExtensionDirectory).ShouldBeTrue();
        Directory.EnumerateFiles(provisioner.ExtensionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureProvisioned_WithoutManifestChecksum_Throws()
    {
        var handler = new FakeHandler(_ => Array.Empty<byte>());
        using var http = new HttpClient(handler);
        var provisioner = new ExtensionProvisioner(_dataRoot, "osx-arm64", http, _ => null);

        var exception = await Should.ThrowAsync<ExtensionProvisioningException>(
            () => provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("No checksum recorded");
        handler.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureProvisioned_ForMuslRid_ThrowsListingMissingBinaries()
    {
        var provisioner = new ExtensionProvisioner(_dataRoot, "linux-musl-x64", new HttpClient(), _ => null, includeCloudSync: true);

        var exception = await Should.ThrowAsync<ExtensionProvisioningException>(
            () => provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("memory-linux-musl-x86_64-full-1.3.5.tar.gz");
        exception.Message.ShouldContain("vector-linux-musl-x86_64-1.0.0.tar.gz");
        exception.Message.ShouldContain("cloudsync-linux-musl-x86_64-1.1.2.tar.gz");
    }

    [Fact]
    public async Task EnsureProvisioned_ForRidWithoutAssets_Throws()
    {
        var provisioner = new ExtensionProvisioner(_dataRoot, "win-arm64", new HttpClient(), _ => null);

        var exception = await Should.ThrowAsync<ExtensionProvisioningException>(
            () => provisioner.EnsureProvisionedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("win-arm64");
    }

    private static Dictionary<string, byte[]> OsxArm64Archives() => new()
    {
        ["vector-macos-arm64-1.0.0.tar.gz"] = TarGz(("vector0.dylib", new byte[] { 1 })),
        ["memory-macos-arm64-full-1.3.5.tar.gz"] = TarGz(("memory0.dylib", new byte[] { 2 })),
        ["cloudsync-macos-arm64-1.1.2.tar.gz"] = TarGz(("sync0.dylib", new byte[] { 3 })),
    };

    private static string AssetName(Uri uri) => Path.GetFileName(uri.AbsolutePath);

    private string ModulePath(string fileName) => Path.Combine(_dataRoot, "extensions", "osx-arm64", fileName);

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccon-provision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] TarGz(params (string Name, byte[] Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var (name, content) in files)
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                    {
                        DataStream = new MemoryStream(content),
                    });
                }
            }
        }

        return buffer.ToArray();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<Uri, byte[]> _bodyFor;

        public FakeHandler(Func<Uri, byte[]> bodyFor) => _bodyFor = bodyFor;

        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_bodyFor(request.RequestUri!)),
            };
            return Task.FromResult(response);
        }
    }
}
