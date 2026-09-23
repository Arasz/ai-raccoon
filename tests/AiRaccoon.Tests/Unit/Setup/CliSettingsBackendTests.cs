using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     The CLI's half of attach-or-start with the identity proof (ADR-0106): every settings-routed
///     verb attaches to a <em>proven</em> listener on <see cref="ServerConfig.Port" />, starts one
///     on the configured port when nothing listens, and falls back to a short-lived private child
///     when the port is held but not proven. Exercised against fake probe/verifier/launcher seams,
///     so the acquire/token/wrap logic is pinned without a real process spawn and without depending
///     on how the test host itself was launched.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliSettingsBackendTests
{
    /// <summary>A packaged apphost: what Environment.ProcessPath names for an installed ai-raccoon.</summary>
    private const string AppHost = "/opt/ai-raccoon/ai-raccoon";

    /// <summary>The dotnet muxer: what Environment.ProcessPath names under `dotnet exec`/`dotnet run`.</summary>
    private const string DotnetHost = "/usr/local/share/dotnet/dotnet";

    private static ServerConfig Config(int port, string dataRoot) =>
        new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = dataRoot, Scope = InstallScope.User });

    [Fact]
    public async Task AcquireAsync_WithAProvenListener_AttachesWithoutSpawning()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-proven");
        try
        {
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var launcher = new ThrowingBackendLauncher();
            var prover = new FakeIdentityProver();
            var logger = new FakeLogger();

            var store = await CliSettingsBackend.AcquireAsync(launcher, prover, new FakeServerProbe(ProbeVerdict.Answered),
                AppHost, Config(54221, dataRoot), logger, TestContext.Current.CancellationToken);

            store.ShouldBeOfType<ServerSettingsStore>();
            prover.Calls[0].ShouldBe(new Uri("http://127.0.0.1:54221/mcp"));
            var disclosure = logger.Collector.GetSnapshot().Single(r => r.Id == 687);
            disclosure.Message.ShouldContain("54221");
            disclosure.Message.ShouldContain("serve --restart --port 54221");
            disclosure.Message.ShouldNotContain("--attach");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WithNoListener_StartsOnTheConfiguredPort()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-cold-port");
        try
        {
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54222/mcp", null));

            var store = await CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(),
                new FakeServerProbe(ProbeVerdict.NotListening), AppHost, Config(54222, dataRoot),
                new FakeLogger(), TestContext.Current.CancellationToken);

            store.ShouldBeOfType<ServerSettingsStore>();
            launcher.AcquireCalls.ShouldBe(1);
            launcher.PrivateCalls.ShouldBe(0);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     The settings half of the re-shaped F70 gate: an unproven listener gets nothing secret;
    ///     the command continues on a bounded private fallback, and the warning names the held port
    ///     and the remedy while the disclosure names the stop command that still exists.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_WithASquatter_FallsBackPrivately_WithABoundedIdleTimeout()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-squatter");
        try
        {
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54223/mcp", null));
            var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
            prover.AnswerNext(null); // the fallback child proves under this root's key
            var logger = new FakeLogger();

            var store = await CliSettingsBackend.AcquireAsync(launcher, prover, new FakeServerProbe(ProbeVerdict.Answered),
                AppHost, Config(54224, dataRoot), logger, TestContext.Current.CancellationToken);

            store.ShouldBeOfType<ServerSettingsStore>();
            launcher.AcquireCalls.ShouldBe(0);
            launcher.PrivateCalls.ShouldBe(1);
            launcher.PrivateArguments[Array.IndexOf(launcher.PrivateArguments, "--port") + 1].ShouldBe("0");
            launcher.PrivateArguments[Array.IndexOf(launcher.PrivateArguments, "--idle-timeout") + 1].ShouldBe("5m");

            var warning = logger.Collector.GetSnapshot().Single(r => r.Id == 690);
            warning.Message.ShouldContain("54224");
            warning.Message.ShouldContain("stop the listener");
            var disclosure = logger.Collector.GetSnapshot().Single(r => r.Id == 687);
            disclosure.Message.ShouldContain("serve --restart --port 54223");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenTheFallbackChildDoesNotProve_ThrowsUnavailable()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-fallback-unproven");
        try
        {
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54225/mcp", null));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(IdentityProofFailure.BadSignature),
                    new FakeServerProbe(ProbeVerdict.Answered), AppHost, Config(54226, dataRoot),
                    new FakeLogger(), TestContext.Current.CancellationToken));

            error.Message.ShouldContain("54226");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenThePortIsOutOfRange_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:0/mcp", null));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.Answered),
                AppHost, Config(0, "/tmp/unused"), new FakeLogger(), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("--port 0");
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheProcessIsTheDotnetHost_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54220/mcp", null));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.Answered),
                DotnetHost, Config(54220, "/tmp/unused"), new FakeLogger(), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("dotnet host");
        error.Message.ShouldContain("serve --port 54220");
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheProcessPathIsUnknown_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54221/mcp", null));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.Answered),
                null, Config(54221, "/tmp/unused"), new FakeLogger(), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("unknown");
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherFindsNoUrl_ThrowsUnavailable_NamingThePort()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("cli-settings-backend-no-url", TestContext.Current.CancellationToken);
        try
        {
            var launcher = new FakeBackendLauncher(new BackendResult(null, 3));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.NotListening),
                    AppHost, Config(54217, dataRoot), new FakeLogger(), TestContext.Current.CancellationToken));

            error.Message.ShouldContain("54217");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherFindsNoUrl_ButCapturedStderr_IncludesItInTheMessage()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("cli-settings-backend-stderr", TestContext.Current.CancellationToken);
        try
        {
            var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "ai-raccoon: could not decrypt the bank"));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.NotListening),
                    AppHost, Config(54219, dataRoot), new FakeLogger(), TestContext.Current.CancellationToken));

            error.Message.ShouldContain("could not decrypt the bank");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherThrowsBackendStart_WrapsAsUnavailable()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("cli-settings-backend-start-throws", TestContext.Current.CancellationToken);
        try
        {
            var launcher = new FakeBackendLauncher(new BackendStartException("could not start it", new InvalidOperationException()));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.NotListening),
                    AppHost, Config(54218, dataRoot), new FakeLogger(), TestContext.Current.CancellationToken));

            error.Message.ShouldContain("could not start it");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenTheDataRootHoldsNoToken_ThrowsUnavailable_NamingTheTokenPath()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-backend-no-token");
        try
        {
            // The bank must exist (F39) while the token deliberately does not — that absence is
            // the verdict this test pins. The listener proves, so the acquire itself succeeds.
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.Answered),
                    AppHost, Config(1, dataRoot), new FakeLogger(), TestContext.Current.CancellationToken));

            error.Message.ShouldContain(McpTokenFile.FileName);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    private sealed class FakeBackendLauncher : IBackendLauncher
    {
        private readonly Exception? _throws;
        private readonly BackendResult _result;

        public FakeBackendLauncher(BackendResult result) => _result = result;
        public FakeBackendLauncher(Exception throws) => _throws = throws;

        public int Calls { get; private set; }

        public int AcquireCalls { get; private set; }

        public int PrivateCalls { get; private set; }

        public string? FileName { get; private set; }

        public string[] PrivateArguments { get; private set; } = [];

        public Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            Calls++;
            PrivateCalls++;
            FileName = fileName;
            PrivateArguments = [.. arguments];
            return _throws is null ? Task.FromResult(_result) : Task.FromException<BackendResult>(_throws);
        }

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            Calls++;
            AcquireCalls++;
            FileName = fileName;
            return _throws is null ? Task.FromResult(_result) : Task.FromException<BackendResult>(_throws);
        }
    }

    /// <summary>Any launcher call is a gate failure: the proven path must never consult it.</summary>
    private sealed class ThrowingBackendLauncher : IBackendLauncher
    {
        public Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ctx) =>
            throw new InvalidOperationException("a proven listener must be attached to, never spawn anything");

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx) =>
            throw new InvalidOperationException("a proven listener must be attached to, never start anything");
    }
}
