using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Infrastructure.Assets;
using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.Download;

/// <summary>
///     WP2 (D4/D8) mocked-HF fixture tests: a local fake HF server serves a small fake repo whose
///     model.onnx is a tiny generated ONNX declaring an external-data sibling. Covers the verified
///     download (verify-or-delete, no half-installed model), LFS-oid pins, SHA-mismatch RED→GREEN,
///     --dry-run, the &gt;500 MB guard (--yes / interactive), the disk-space guard, the ORT opset
///     smoke failure path, external-data pairing end-to-end, and the manifest writer.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ModelDownloadServiceTests : IDisposable
{
    private readonly FakeHfServer _server = FakeHfServer.Start();
    private readonly string _targetDir = Path.Combine(Path.GetTempPath(), "ai-raccoon-dl-tests", Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task HappyPath_DownloadsAllFiles_VerifiesPins_WritesManifest()
    {
        var repo = FakeRepo.BgeM3(_server);

        var result = await Service(repo).DownloadAsync(Request(repo), TestContext.Current.CancellationToken);

        // Files land flat in the slug dir with the exact served bytes.
        File.ReadAllBytes(Path.Combine(_targetDir, "model.onnx")).ShouldBe(repo.OnnxBytes);
        File.ReadAllBytes(Path.Combine(_targetDir, "model.onnx_data")).ShouldBe(repo.DataBytes);
        File.ReadAllBytes(Path.Combine(_targetDir, "sentencepiece.bpe.model")).ShouldBe(repo.SpmBytes);
        File.Exists(Path.Combine(_targetDir, "config.json")).ShouldBeTrue();
        File.Exists(Path.Combine(_targetDir, "tokenizer_config.json")).ShouldBeTrue();
        result.DownloadedFiles.ShouldContain("model.onnx");

        var manifest = LoadManifest();
        manifest.Source.ShouldBe(new ManifestSource("test/fake-model", "main"));
        manifest.Dimensions.ShouldBe(1024);
        manifest.ContextWindowTokens.ShouldBe(8192);
        manifest.Tokenizer.Family.ShouldBe(TokenizerFamily.SentencePiece);
        manifest.Tokenizer.Files.Single().Path.ShouldBe("sentencepiece.bpe.model");
        manifest.Tokenizer.Files.Single().Sha256.ShouldBe(repo.SpmSha);
        manifest.Tokenizer.Options!.SpecialTokens!["<s>"].ShouldBe(0);
        manifest.Tokenizer.Options!.SpecialTokens!["<pad>"].ShouldBe(1);
        manifest.Tokenizer.Options!.SpecialTokens!["</s>"].ShouldBe(2);
        manifest.Tokenizer.Options!.SpecialTokens!["<unk>"].ShouldBe(3);
        manifest.Onnx.Inputs.ShouldBe(["input_ids", "attention_mask"]);
        manifest.Onnx.EmbeddingOutput.ShouldBe("sentence_embedding");
        manifest.Onnx.TokenEmbeddingsOutput.ShouldBe("token_embeddings");
        // External-data pairing from the generated ONNX protobuf lands in the manifest with pins.
        manifest.Onnx.Files.Select(f => f.Path).ShouldBe(["model.onnx", "model.onnx_data"]);
        manifest.Onnx.Files.Single(f => f.Path == "model.onnx").Sha256.ShouldBe(repo.OnnxSha);
        manifest.Onnx.Files.Single(f => f.Path == "model.onnx_data").Sha256.ShouldBe(repo.DataSha);
        // D11: no 1_Pooling layout in the fake repo → placeholder pooling.
        manifest.Pooling.Mode.ShouldBe(PoolingMode.ModelOutput);
        manifest.Pooling.OutputNames!.Embedding.ShouldBe("sentence_embedding");
        new EmbeddingManifestValidator().Validate(manifest).ShouldBeEmpty();
    }

    /// <summary>
    ///     Regression: a repo's own sentence-transformers 1_Pooling/config.json must never be
    ///     mistaken for the model's config.json — dims/ctx still come from onnx/config.json and
    ///     the pooling layout is read separately (the real BAAI/bge-m3 ships both).
    /// </summary>
    [Fact]
    public async Task PoolingLayoutInRepo_DoesNotShadowTheModelConfig()
    {
        var repo = FakeRepo.BgeM3(_server, withSentenceTransformersLayout: true);

        var result = await Service(repo).DownloadAsync(Request(repo), TestContext.Current.CancellationToken);

        result.Plan.Dimensions.ShouldBe(1024);
        result.Plan.ContextWindowTokens.ShouldBe(8192);
        result.Plan.PoolingMode.ShouldBe(PoolingMode.Cls);
        result.Plan.PoolingProvenance.ShouldBe("sentence-transformers");
        File.Exists(Path.Combine(_targetDir, EmbeddingManifest.FileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task ShaMismatch_DeletesArtifact_LeavesNoManifest_Throws()
    {
        var repo = FakeRepo.BgeM3(_server, corruptOnnx: true);

        var ex = await Should.ThrowAsync<ModelDownloadException>(
            () => Service(repo).DownloadAsync(Request(repo), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("sha256");
        ex.Message.ShouldContain(repo.OnnxSha);
        // verify-or-delete: no half-installed model, no .part residue, no manifest.
        File.Exists(Path.Combine(_targetDir, "model.onnx")).ShouldBeFalse();
        Directory.Exists(_targetDir).ShouldBeFalse();
        File.Exists(Path.Combine(_targetDir, EmbeddingManifest.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task AlreadyDownloaded_VerifiedFiles_AreSkipped()
    {
        var repo = FakeRepo.BgeM3(_server);
        Directory.CreateDirectory(_targetDir);
        File.WriteAllBytes(Path.Combine(_targetDir, "model.onnx"), repo.OnnxBytes);
        File.WriteAllBytes(Path.Combine(_targetDir, "model.onnx_data"), repo.DataBytes);
        File.WriteAllBytes(Path.Combine(_targetDir, "sentencepiece.bpe.model"), repo.SpmBytes);

        var result = await Service(repo).DownloadAsync(Request(repo), TestContext.Current.CancellationToken);

        // LFS-pinned files verify against their oid and are skipped; the small TOFU provenance
        // files (config.json/tokenizer_config.json) carry no pin and are re-fetched.
        result.DownloadedFiles.ShouldBe(["config.json", "tokenizer_config.json"]);
        _server.Hits.ShouldNotContain(h => h.Key.EndsWith("/model.onnx", StringComparison.Ordinal) && h.Key.Contains("/resolve/", StringComparison.Ordinal));
        _server.Hits.ShouldNotContain(h => h.Key.EndsWith("/model.onnx_data", StringComparison.Ordinal) && h.Key.Contains("/resolve/", StringComparison.Ordinal));
        // The manifest is (re)written so provenance is complete.
        File.Exists(Path.Combine(_targetDir, EmbeddingManifest.FileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task DryRun_ResolvesAndPrintsSizesAndOids_WithoutDownloading()
    {
        var repo = FakeRepo.BgeM3(_server);

        var result = await Service(repo).DownloadAsync(
            Request(repo) with { DryRun = true }, TestContext.Current.CancellationToken);

        result.ManifestPath.ShouldBeNull();
        result.DownloadedFiles.ShouldBeEmpty();
        Directory.Exists(_targetDir).ShouldBeFalse();
        var model = result.Plan.Files.Single(f => f.Path == "onnx/model.onnx");
        model.Size.ShouldBe(repo.OnnxBytes.Length);
        model.LfsSha256.ShouldBe(repo.OnnxSha);
        // External data is enumerated by glob in dry-run (no bytes are downloaded to probe).
        result.Plan.Files.Single(f => f.Path == "onnx/model.onnx_data").LfsSha256.ShouldBe(repo.DataSha);
    }

    [Fact]
    public async Task LargeDownload_WithoutYes_Refuses_BeforeAnyDownload()
    {
        var repo = FakeRepo.BgeM3(_server, declaredDataSize: 600L * 1024 * 1024);

        var ex = await Should.ThrowAsync<ModelDownloadRejectedException>(
            () => Service(repo).DownloadAsync(Request(repo), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("600");
        ex.Message.ShouldContain("--yes");
        Directory.Exists(_targetDir).ShouldBeFalse();
    }

    [Fact]
    public async Task LargeDownload_PromptNo_Refuses_PromptYes_Proceeds()
    {
        var repo = FakeRepo.BgeM3(_server, declaredDataSize: 600L * 1024 * 1024);
        var service = Service(repo);

        var refused = await Should.ThrowAsync<ModelDownloadRejectedException>(() =>
            service.DownloadAsync(Request(repo, confirm: _ => false), TestContext.Current.CancellationToken));
        refused.Message.ShouldContain("--yes");
        Directory.Exists(_targetDir).ShouldBeFalse();

        var accepted = await service.DownloadAsync(Request(repo, confirm: _ => true), TestContext.Current.CancellationToken);
        accepted.ManifestPath.ShouldNotBeNull();
        File.Exists(Path.Combine(_targetDir, "model.onnx_data")).ShouldBeTrue();
    }

    [Fact]
    public async Task InsufficientDiskSpace_Refuses_BeforeAnyDownload()
    {
        var repo = FakeRepo.BgeM3(_server, declaredDataSize: 10L * 1024 * 1024);
        var service = Service(repo, disk: new FakeDiskSpace(1024));

        var ex = await Should.ThrowAsync<ModelDownloadRejectedException>(
            () => service.DownloadAsync(Request(repo, confirm: _ => true), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("disk space");
        Directory.Exists(_targetDir).ShouldBeFalse();
    }

    [Fact]
    public async Task OrtsmokeFailure_Fails_AfterDownload_NoManifest_NoLeftovers()
    {
        var repo = FakeRepo.BgeM3(_server);
        var service = Service(repo, smoke: new FakeSmokeTester(ok: false));

        var ex = await Should.ThrowAsync<ModelDownloadException>(
            () => service.DownloadAsync(Request(repo), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("ONNX Runtime");
        File.Exists(Path.Combine(_targetDir, EmbeddingManifest.FileName)).ShouldBeFalse();
        Directory.Exists(_targetDir).ShouldBeFalse();
    }

    [Fact]
    public async Task MidDownloadFailure_CleansUpThisRunsFiles()
    {
        // The tokenizer file's served bytes do not match its tree oid: files downloaded earlier
        // in the run (model.onnx, model.onnx_data) must be removed too — no half-installed model.
        var repo = FakeRepo.BgeM3(_server, corruptSpm: true);

        await Should.ThrowAsync<ModelDownloadException>(
            () => Service(repo).DownloadAsync(Request(repo), TestContext.Current.CancellationToken));

        Directory.Exists(_targetDir).ShouldBeFalse();
    }

    [Fact]
    public async Task ExplicitFile_OverridesAutoSelection()
    {
        // Repo with the model at the ROOT (model.onnx + model.onnx_data + vocab.txt, bert layout);
        // --file selects the root file even though onnx/model.onnx also exists.
        var repo = FakeRepo.BertRoot(_server);

        var result = await Service(repo).DownloadAsync(
            Request(repo, explicitFiles: ["model.onnx"]), TestContext.Current.CancellationToken);

        result.Plan.ModelFilePath.ShouldBe("model.onnx");
        File.Exists(Path.Combine(_targetDir, "model.onnx")).ShouldBeTrue();
        File.Exists(Path.Combine(_targetDir, "model.onnx_data")).ShouldBeTrue();
        File.Exists(Path.Combine(_targetDir, "vocab.txt")).ShouldBeTrue();
        LoadManifest().Tokenizer.Family.ShouldBe(TokenizerFamily.BertWordpiece);
    }

    /// <summary>
    ///     Issue #417: faxenoff/code-daemon-embed-v1's tokenizer_config.json ships no
    ///     added_tokens_decoder. The service must NOT refuse the download — it derives the
    ///     special-token ids from the downloaded sp model's own piece table (D1-compatible option
    ///     2) once the tokenizer file is on disk.
    /// </summary>
    [Fact]
    public async Task SentencePieceModel_WithoutAddedTokensDecoder_DerivesSpecialTokensFromPieceTable()
    {
        var repo = FakeRepo.CodeDaemon(_server);

        var result = await Service(repo).DownloadAsync(Request(repo), TestContext.Current.CancellationToken);

        result.ManifestPath.ShouldNotBeNull();
        var manifest = LoadManifest();
        manifest.Tokenizer.Family.ShouldBe(TokenizerFamily.SentencePiece);
        manifest.Tokenizer.Options!.SpecialTokens!["<s>"].ShouldBe(2);
        manifest.Tokenizer.Options!.SpecialTokens!["</s>"].ShouldBe(3);
        manifest.Tokenizer.Options!.SpecialTokens!["<pad>"].ShouldBe(0);
        manifest.Tokenizer.Options!.SpecialTokens!["<unk>"].ShouldBe(1);
    }

    /// <summary>Negative control: D1 still applies even through the sp-derivation fallback — a
    /// declared token whose content is not a piece in the sp model's vocabulary is still refused,
    /// naming the missing piece, not guessed.</summary>
    [Fact]
    public async Task SentencePieceModel_WithoutAddedTokensDecoder_PieceMissingFromVocabulary_Fails()
    {
        var repo = FakeRepo.CodeDaemon(_server, declareUnresolvablePiece: true);

        var ex = await Should.ThrowAsync<ModelDownloadException>(
            () => Service(repo).DownloadAsync(Request(repo), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("<not-a-real-piece>");
        Directory.Exists(_targetDir).ShouldBeFalse();
        File.Exists(Path.Combine(_targetDir, EmbeddingManifest.FileName)).ShouldBeFalse();
    }

    [Fact]
    public void ModelSlug_SanitizesRepoId()
    {
        ModelSlug.Sanitize("BAAI/bge-m3").ShouldBe("BAAI__bge-m3");
        ModelSlug.Sanitize("a/b/c").ShouldBe("a__b__c");
        ModelSlug.Sanitize("has space/model").ShouldBe("has_space__model");
    }

    private ModelDownloadRequest Request(FakeRepo repo, IReadOnlyList<string>? explicitFiles = null,
        Func<string, bool>? confirm = null) =>
        new(repo.RepoId, repo.Revision, _targetDir, explicitFiles, DryRun: false, Yes: false, confirm);

    private ModelDownloadService Service(FakeRepo repo, IOnnxSmokeTester? smoke = null, IDiskSpaceProvider? disk = null)
    {
        var http = new HttpClient();
        return new ModelDownloadService(
            new HfTreeClient(http, _server.BaseUrl),
            new AssetDownloader(http),
            new ModelDownloadPlanner(),
            new OnnxGraphProbeReader(),
            smoke ?? new FakeSmokeTester(ok: true),
            disk ?? new FakeDiskSpace(long.MaxValue),
            new EmbeddingManifestSerializer(),
            new EmbeddingManifestValidator(),
            new SentencePieceVocabularyReader());
    }

    private EmbeddingManifest LoadManifest() =>
        new EmbeddingManifestSerializer().Deserialize(File.ReadAllText(Path.Combine(_targetDir, EmbeddingManifest.FileName)));

    private sealed class FakeSmokeTester(bool ok) : IOnnxSmokeTester
    {
        public void Verify(string onnxPath)
        {
            if (!ok)
            {
                throw new OnnxSmokeTestException("simulated ONNX Runtime load failure");
            }
        }
    }

    private sealed class FakeDiskSpace(long free) : IDiskSpaceProvider
    {
        public long AvailableFreeBytes(string path) => free;
    }

}
