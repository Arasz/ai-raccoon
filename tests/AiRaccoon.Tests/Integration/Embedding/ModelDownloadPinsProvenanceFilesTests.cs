using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Infrastructure.Assets;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Tests.Unit.Embedding.Download;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     D2: the non-LFS provenance files (config.json, tokenizer_config.json) are downloaded and
///     hashed to disk already, but never reached the manifest's file lists — D1's pin verification
///     could not see them. This proves they now land in the manifest with a sha256, and that D1
///     then catches a post-download edit of either file.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ModelDownloadPinsProvenanceFilesTests : IDisposable
{
    private readonly FakeHfServer _server = FakeHfServer.Start();
    private readonly string _targetDir = Path.Combine(Path.GetTempPath(), "ai-raccoon-provenance-pin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_targetDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private static string Sha(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private ModelDownloadService Service()
    {
        var http = new HttpClient();
        return new ModelDownloadService(
            new HfTreeClient(http, _server.BaseUrl),
            new AssetDownloader(http),
            new ModelDownloadPlanner(),
            new OnnxGraphProbeReader(),
            new PassingSmokeTester(),
            new UnlimitedDiskSpace(),
            new EmbeddingManifestSerializer(),
            new EmbeddingManifestValidator(),
            new SentencePieceVocabularyReader());
    }

    private EmbeddingManifest LoadManifest() =>
        new EmbeddingManifestSerializer().Deserialize(File.ReadAllText(Path.Combine(_targetDir, EmbeddingManifest.FileName)));

    [Fact]
    public async Task TheProvenanceFiles_AppearInTheManifestWithSha256()
    {
        var repo = FakeRepo.BgeM3(_server);
        var request = new ModelDownloadRequest(repo.RepoId, repo.Revision, _targetDir);

        await Service().DownloadAsync(request, TestContext.Current.CancellationToken);

        var manifest = LoadManifest();
        manifest.ProvenanceFiles.ShouldNotBeNull();
        manifest.ProvenanceFiles!.ShouldContain(f => f.Path == "config.json" && f.Sha256 == Sha(repo.ConfigJson));
        manifest.ProvenanceFiles!.ShouldContain(f => f.Path == "tokenizer_config.json" && f.Sha256 == Sha(repo.TokenizerConfigJson));
    }

    [Fact]
    public async Task EditingConfigJsonAfterDownload_FailsActivation()
    {
        var repo = FakeRepo.BgeM3(_server);
        var request = new ModelDownloadRequest(repo.RepoId, repo.Revision, _targetDir);
        await Service().DownloadAsync(request, TestContext.Current.CancellationToken);

        File.WriteAllText(Path.Combine(_targetDir, "config.json"), "{\"tampered\": true}");

        var ex = Should.Throw<InvalidOperationException>(() =>
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(_targetDir));

        ex.Message.ShouldContain("config.json");
    }

    private sealed class PassingSmokeTester : IOnnxSmokeTester
    {
        public void Verify(string onnxPath)
        {
        }
    }

    private sealed class UnlimitedDiskSpace : IDiskSpaceProvider
    {
        public long AvailableFreeBytes(string path) => long.MaxValue;
    }
}
