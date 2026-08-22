using System.Net;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Delta review C2 (surface F3): a server-side 5xx on the settings channel must exit with a
///     code distinct from <see cref="ExitCode.InvalidArgument" /> ("you mistyped") — the server
///     broke, not the caller. Drives the real <see cref="ServerSettingsStore" /> (its
///     <c>Ensure</c>/<c>SendAsync</c> path) through <see cref="ConfigCommands" /> at argv level,
///     against a stubbed HTTP channel, the same shape <c>ServerProbeResilienceTests</c> uses.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SettingsChannelExitCodeTests
{
    [Fact]
    public async Task AServerSide500_ExitsWithItsOwnCode()
    {
        var serverStore = new ServerSettingsStore(
            new HttpClient(new StubHandler(HttpStatusCode.InternalServerError))
            {
                BaseAddress = new Uri("http://127.0.0.1:1/")
            },
            "test-token");
        var commands = TestData.CreateConfigCommands(new SettingsRoutedStore(serverStore), settings: new SettingsCommands());

        var (exit, _, err) = await CliRun.RunAsync(["settings", "sweep", "show"], commands);

        exit.ShouldBe(ExitCode.SettingsServerError);
        err.ShouldContain("500");
        err.ShouldNotContain("you mistyped");
    }

    /// <summary>A genuine usage error must stay untouched by the new mapping.</summary>
    [Fact]
    public async Task AGenuineBadArgument_StillExitsWithInvalidArgument()
    {
        var (exit, _, err) = await CliRun.RunAsync(["settings", "retrieval", "alpha", "set", "abc"],
            TestData.CreateConfigCommands(new FakeConfigStore(), settings: new SettingsCommands()));

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldNotBeEmpty();
    }

    /// <summary>
    ///     Found by the 1.32.0 post-publish check after #476: the chunk-budget refusal is the one
    ///     leg SettingsCommands does not pre-check itself, so ServerSettingsStore.ActivateCodeEngineAsync
    ///     is what has to surface the server's reason. This asserts the CLI-facing side of that fix —
    ///     the exception lands in ConfigCommands' catch-all and exits 15 with the reason in stderr,
    ///     not "Response status code does not indicate success: 400 (Bad Request)".
    /// </summary>
    [Fact]
    public async Task ModelSetCodeLocal_OnACodeEngineActivationRefusal_ExitsInvalidArgument_WithTheReason()
    {
        var dir = TestData.CreateTempRoot("ai-raccoon-model-set-code-local");
        try
        {
            File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
            File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
            File.Copy(TestData.RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1.json"),
                Path.Combine(dir, EmbeddingManifest.FileName));
            var reason = $"Manifest '{dir}' resolves to a 126-token chunk budget, narrower than the " +
                         $"{CodeChunker.DefaultBudget}-token chunks the code corpus's chunker emits";
            var codeEngine = new ThrowingCodeEngineStore(new CodeEngineActivationRefusedException(reason));
            var commands = TestData.CreateConfigCommands(new FakeConfigStore(), settings: new SettingsCommands(),
                codeEngine: codeEngine);

            var (exit, _, err) = await CliRun.RunAsync(["model", "set", "code", "local", dir], commands);

            exit.ShouldBe(ExitCode.InvalidArgument);
            err.ShouldContain(CodeChunker.DefaultBudget.ToString());
            err.ShouldNotContain("does not indicate success");
        }
        finally
        {
            TestData.DeleteTempRoot(dir);
        }
    }

    /// <summary>Routes settings calls to the injected <see cref="ISettingsStore" /> exactly like
    /// <c>LazyServerSettingsStore</c> does in production — the fake stands in for that indirection.</summary>
    private sealed class SettingsRoutedStore(ISettingsStore inner) : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.GetSettingAsync(key, cancellationToken);
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    /// <summary>Stands in for ServerSettingsStore's 400 mapping (already proven at the HTTP level in
    /// SettingsEndpointTests) so this test isolates just the CLI-side exit-code/message mapping.</summary>
    private sealed class ThrowingCodeEngineStore(Exception toThrow) : ICodeEngineStore
    {
        public Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory,
            CancellationToken cancellationToken = default) =>
            throw toThrow;
    }
}
