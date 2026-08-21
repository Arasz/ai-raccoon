using System.Net.Http;
using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Embedding.Download;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     WP2 CLI verb tests against the mocked-HF fixture server: exit codes, stdout/stderr
///     routing, the --dry-run listing (sizes + oids, nothing downloaded), the &gt;500 MB guard
///     with interactive confirmation, and the SHA-mismatch failure path (exit non-zero).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ModelDownloadCommandTests : IDisposable
{
    private readonly FakeHfServer _server = FakeHfServer.Start();
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "ai-raccoon-cli-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task Download_EndToEnd_ExitZero_PrintsTargetAndActivationHint()
    {
        var repo = FakeRepo.BgeM3(_server);

        var (exit, @out, err) = await Run(repo, ["model", "download", repo.RepoId]);

        exit.ShouldBe(ExitCode.Success);
        @out.ShouldContain($"downloaded {repo.RepoId}");
        @out.ShouldContain("manifest.json");
        @out.ShouldContain("model set local");
        err.ShouldBeEmpty();
        var targetDir = Path.Combine(_dataRoot, "models", ModelSlug.Sanitize(repo.RepoId));
        File.Exists(Path.Combine(targetDir, "manifest.json")).ShouldBeTrue();
        File.Exists(Path.Combine(targetDir, "model.onnx_data")).ShouldBeTrue();
    }

    [Fact]
    public async Task DryRun_ExitZero_ListsSizesAndOids_WithoutDownloading()
    {
        var repo = FakeRepo.BgeM3(_server);

        var (exit, @out, err) = await Run(repo, ["model", "download", repo.RepoId, "--dry-run"]);

        exit.ShouldBe(ExitCode.Success);
        @out.ShouldContain("onnx/model.onnx");
        @out.ShouldContain("onnx/model.onnx_data");
        @out.ShouldContain(repo.OnnxSha[..16]);
        @out.ShouldContain(repo.DataSha[..16]);
        @out.ShouldContain("dims 1024");
        err.ShouldBeEmpty();
        Directory.Exists(Path.Combine(_dataRoot, "models", ModelSlug.Sanitize(repo.RepoId))).ShouldBeFalse();
    }

    [Fact]
    public async Task ShaMismatch_ExitNonZero_WithActionableMessage()
    {
        var repo = FakeRepo.BgeM3(_server, corruptOnnx: true);

        var (exit, @out, err) = await Run(repo, ["model", "download", repo.RepoId]);

        exit.ShouldBe(ExitCode.ModelDownloadFailed);
        @out.ShouldBeEmpty();
        err.ShouldContain("sha256 mismatch");
        err.ShouldContain(repo.OnnxSha);
    }

    [Fact]
    public async Task MissingRepo_ExitNonZero_WithActionableMessage()
    {
        var repo = FakeRepo.BgeM3(_server);

        var (exit, _, err) = await Run(repo, ["model", "download", "nope/missing"]);

        exit.ShouldBe(ExitCode.ModelDownloadFailed);
        err.ShouldContain("nope/missing");
    }

    [Fact]
    public async Task LargeDownload_StdinNo_Refuses_ExitInvalidArgument()
    {
        var repo = FakeRepo.BgeM3(_server, declaredDataSize: 600L * 1024 * 1024);

        var (exit, @out, err) = await Run(repo, ["model", "download", repo.RepoId], stdin: "n\n");

        exit.ShouldBe(ExitCode.InvalidArgument);
        @out.ShouldBeEmpty();
        err.ShouldContain("--yes");
        Directory.Exists(Path.Combine(_dataRoot, "models", ModelSlug.Sanitize(repo.RepoId))).ShouldBeFalse();
    }

    [Fact]
    public async Task LargeDownload_StdinYes_Proceeds_ExitZero()
    {
        var repo = FakeRepo.BgeM3(_server, declaredDataSize: 600L * 1024 * 1024);

        var (exit, _, err) = await Run(repo, ["model", "download", repo.RepoId], stdin: "y\n");

        exit.ShouldBe(ExitCode.Success);
        err.ShouldContain("[y/N]");
        File.Exists(Path.Combine(_dataRoot, "models", ModelSlug.Sanitize(repo.RepoId), "manifest.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task LargeDownload_YesFlag_SkipsThePrompt()
    {
        var repo = FakeRepo.BgeM3(_server, declaredDataSize: 600L * 1024 * 1024);

        var (exit, _, err) = await Run(repo, ["model", "download", repo.RepoId, "--yes"]);

        exit.ShouldBe(ExitCode.Success);
        err.ShouldNotContain("[y/N]");
        File.Exists(Path.Combine(_dataRoot, "models", ModelSlug.Sanitize(repo.RepoId), "manifest.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Dispatch_ThroughConfigCommands_RoutesToTheDownloadVerb()
    {
        var repo = FakeRepo.BgeM3(_server);
        var httpFactory = new SingletonHttpClientFactory(new HttpClient());
        var modelDownload = new ModelDownloadCommands(httpFactory, new FakeSmokeTester(ok: true), new FakeDiskSpace(long.MaxValue), _server.BaseUrl);
        var commands = TestData.CreateConfigCommands(store: null!, modelDownload: modelDownload);

        var (exit, @out, _) = await CliRun.RunAsync(["model", "download", repo.RepoId, "--dry-run"], commands);

        exit.ShouldBe(ExitCode.Success);
        @out.ShouldContain("onnx/model.onnx");
    }

    [Fact]
    public async Task DirFlag_OverridesTheDefaultSlugTarget()
    {
        var repo = FakeRepo.BgeM3(_server);
        var customDir = Path.Combine(_dataRoot, "elsewhere");

        var (exit, @out, _) = await Run(repo, ["model", "download", repo.RepoId, "--dir", customDir]);

        exit.ShouldBe(ExitCode.Success);
        @out.ShouldContain(customDir);
        File.Exists(Path.Combine(customDir, "manifest.json")).ShouldBeTrue();
    }

    private async Task<(int Exit, string Out, string Err)> Run(FakeRepo repo, string[] args, string? stdin = null)
    {
        var httpFactory = new SingletonHttpClientFactory(new HttpClient());
        var commands = new ModelDownloadCommands(httpFactory, new FakeSmokeTester(ok: true), new FakeDiskSpace(long.MaxValue), _server.BaseUrl);
        return await CliRun.RunAsync(args, (parsed, streams, ct) =>
            commands.RunAsync(parsed.ParsedCliArgs, _dataRoot, streams, ct), stdin is null ? null : new StringReader(stdin));
    }

    private sealed class SingletonHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

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
