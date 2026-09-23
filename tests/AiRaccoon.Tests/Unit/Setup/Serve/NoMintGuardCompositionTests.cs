using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     F39 no-mint guard, exercised at the two client auto-launch composition roots
///     (<see cref="ProxyRunner" />, and the settings-routed <c>AppRunner</c> path over
///     <see cref="CliSettingsBackend.AcquireAsync(AiRaccoon.Hosting.Common.ServerConfig, ILoggerFactory, CancellationToken)" />):
///     an empty, explicit, non-default root refuses before any probe or spawn, exits 22, and mints
///     nothing. The real launchers are wired (as production does) but never reached — the guard fires
///     first — so this stays fast without a real process spawn.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoMintGuardCompositionTests
{
    /// <summary>A packaged apphost: what Environment.ProcessPath names for an installed ai-raccoon — the explicit seam these tests use so the verdict never depends on how the test host itself was launched (the dotnet muxer cannot be a backend).</summary>
    private const string AppHost = "/opt/ai-raccoon/ai-raccoon";

    [Fact]
    public async Task ProxyAcquire_AgainstAnEmptyExplicitRoot_Exits22_AndCreatesNothing()
    {
        var dataRoot = TestData.CreateTempRoot("no-mint-guard-proxy");
        try
        {
            using var lease = LoopbackPort.Reserve();
            var port = lease.Port;
            lease.Dispose();
            var stderr = new StringWriter();
            var config = new ServerConfig(port, McpTransport.Proxy, TestData.CreateInfrastructureOptions(dataRoot));

            var exit = await TestData.CreateProxyRunner().RunAsync(config,
                new StandardStreams(TextReader.Null, TextWriter.Null, stderr), AppHost, TestContext.Current.CancellationToken);

            exit.ShouldBe(ExitCode.NoBank);
            stderr.ToString().ShouldContain(dataRoot);
            new DirectoryInfo(dataRoot).EnumerateFileSystemInfos().ShouldBeEmpty(
                "a refused auto-launch must mint nothing under the resolved root");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task SettingsVerb_AgainstAnEmptyExplicitRoot_Exits22_AndCreatesNothing()
    {
        var dataRoot = TestData.CreateTempRoot("no-mint-guard-settings");
        try
        {
            var (exit, stderr) = await RunSettingsShowAsync(dataRoot);

            exit.ShouldBe(ExitCode.NoBank);
            stderr.ShouldContain(dataRoot);
            new DirectoryInfo(dataRoot).EnumerateFileSystemInfos().ShouldBeEmpty(
                "a refused auto-launch must mint nothing under the resolved root");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     `model code set default` downloads the default code model before it touches the settings
    ///     store, so the store's guard used to run only after the model had landed under the mistyped
    ///     root. The guard runs before the download: exit 22, nothing fetched, nothing created.
    /// </summary>
    [Fact]
    public async Task ModelCodeSetDefault_AgainstAnEmptyExplicitRoot_Exits22_BeforeDownloadingAnything()
    {
        var dataRoot = TestData.CreateTempRoot("no-mint-guard-code-default");
        try
        {
            var commands = TestData.CreateConfigCommands(new FakeMemoryStore(), settings: new SettingsCommands(),
                modelDownload: new ModelDownloadCommands(new NoDownloadHttpClientFactory()));

            var (exit, _, stderr) = await CliRun.RunAsync(["--data-root", dataRoot, "model", "code", "set", "default"], commands);

            exit.ShouldBe(ExitCode.NoBank, stderr);
            stderr.ShouldContain(dataRoot);
            new DirectoryInfo(dataRoot).EnumerateFileSystemInfos().ShouldBeEmpty(
                "a refused auto-launch verb must fetch and create nothing under the resolved root");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task NoBank_MapsTo22_InBothCompositionRoots()
    {
        var proxyRoot = TestData.CreateTempRoot("no-mint-guard-map-proxy");
        var settingsRoot = TestData.CreateTempRoot("no-mint-guard-map-settings");
        try
        {
            using var lease = LoopbackPort.Reserve();
            var port = lease.Port;
            lease.Dispose();
            var config = new ServerConfig(port, McpTransport.Proxy, TestData.CreateInfrastructureOptions(proxyRoot));

            var proxyExit = await TestData.CreateProxyRunner().RunAsync(config,
                new StandardStreams(TextReader.Null, TextWriter.Null, TextWriter.Null), AppHost,
                TestContext.Current.CancellationToken);

            var (settingsExit, _) = await RunSettingsShowAsync(settingsRoot);

            proxyExit.ShouldBe(ExitCode.NoBank);
            settingsExit.ShouldBe(ExitCode.NoBank);
        }
        finally
        {
            TestData.DeleteTempRoot(proxyRoot);
            TestData.DeleteTempRoot(settingsRoot);
        }
    }

    /// <summary>Runs `settings sweep show` through the real production acquire delegate, with the
    /// process path pinned so the dotnet test-host muxer never masks the guard under test — mirrors
    /// QuietLoggingTests' Console-redirection discipline since AppRunner's default streams are the
    /// real, process-global Console.</summary>
    private static async Task<(int Exit, string Stderr)> RunSettingsShowAsync(string dataRoot)
    {
        await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            try
            {
                var exit = await new AppRunner(CliSettingsBackend.AcquireAsync, AppHost).Run(
                    ["--data-root", dataRoot, "settings", "sweep", "show"]);
                return (exit, errWriter.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
        finally
        {
            TestData.EnvVarGate.Release();
        }
    }

    /// <summary>A refused verb must never reach the network; handing out a client would hide that it tried.</summary>
    private sealed class NoDownloadHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("a verb refused for a missing bank must not start a download");
    }
}
