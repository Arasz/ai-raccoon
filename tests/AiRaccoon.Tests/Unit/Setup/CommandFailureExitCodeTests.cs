using System.Net;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     What a command's failure exits with once argv has parsed: only a rejected value is
///     InvalidArgument (15); a bank that cannot be opened is FailedToOpenEncryptedBank (2), a
///     Ctrl-C is Interrupted (130), and anything else is CommandFailed (27), never "you mistyped".
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CommandFailureExitCodeTests
{
    [Fact]
    public async Task AnUnexpectedFault_ExitsCommandFailed_NotInvalidArgument()
    {
        var (exit, _, err) = await RunWithStoreThrowing(new InvalidOperationException("boom"));

        exit.ShouldBe(ErrorCode.Internal.Unexpected);
        err.ShouldContain("boom");
    }

    /// <summary>An HttpClient timeout surfaces as a cancellation the caller never asked for.</summary>
    [Fact]
    public async Task ACancellationTheCallerDidNotAskFor_ExitsCommandFailed()
    {
        var (exit, _, _) = await RunWithStoreThrowing(new TaskCanceledException("timed out"));

        exit.ShouldBe(ErrorCode.Internal.Unexpected);
    }

    [Fact]
    public async Task ABusyBank_ExitsFailedToOpenTheBank()
    {
        var (exit, _, err) = await RunWithStoreThrowing(new SqliteException("database is locked", 5));

        exit.ShouldBe(ErrorCode.Bank.OpenFailed);
        err.ShouldContain("database is locked");
    }

    [Fact]
    public async Task ABankTheKeyDoesNotOpen_ExitsFailedToOpenTheBank()
    {
        var (exit, _, _) = await RunWithStoreThrowing(new BankKeyMismatchException("the key does not open the bank"));

        exit.ShouldBe(ErrorCode.Bank.OpenFailed);
    }

    [Fact]
    public async Task ARejectedValue_StillExitsInvalidArgument()
    {
        var (exit, _, _) = await RunWithStoreThrowing(new ArgumentException("bad key"));

        exit.ShouldBe(ErrorCode.Usage.InvalidValue);
    }

    /// <summary>The settings server answers 400 only when it rejects what the command sent.</summary>
    [Fact]
    public async Task AServer400_ExitsInvalidArgument()
    {
        var serverStore = new ServerSettingsStore(
            new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))))
            {
                BaseAddress = new Uri("http://127.0.0.1:1/")
            },
            "test-token");

        var (exit, _, _) = await CliRun.RunAsync(["settings", "sweep", "show"],
            TestData.CreateConfigCommands(new SettingsRoutedStore(serverStore), settings: new SettingsCommands()));

        exit.ShouldBe(ErrorCode.Usage.InvalidValue);
    }

    [Fact]
    public async Task AnyOtherServer4xx_ExitsCommandFailed()
    {
        var serverStore = new ServerSettingsStore(
            new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))))
            {
                BaseAddress = new Uri("http://127.0.0.1:1/")
            },
            "test-token");

        var (exit, _, _) = await CliRun.RunAsync(["settings", "sweep", "show"],
            TestData.CreateConfigCommands(new SettingsRoutedStore(serverStore), settings: new SettingsCommands()));

        exit.ShouldBe(ErrorCode.Internal.Unexpected);
    }

    /// <summary>Ctrl-C while the settings request is in flight: the caller's own token fires.</summary>
    [Fact]
    public async Task CtrlCDuringASettingsRequest_ExitsInterrupted_NotServerUnavailable()
    {
        using var cts = new CancellationTokenSource();
        var serverStore = new ServerSettingsStore(
            new HttpClient(new StubHandler(async (_, ct) =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }))
            {
                BaseAddress = new Uri("http://127.0.0.1:1/")
            },
            "test-token");
        var commands = TestData.CreateConfigCommands(new SettingsRoutedStore(serverStore), settings: new SettingsCommands());

        var (exit, _, err) = await CliRun.RunAsync(["settings", "sweep", "show"],
            (parsed, streams, _) => commands.RunAsync(parsed, streams, cts.Token));

        exit.ShouldBe(ErrorCode.Ok.SIGC);
        err.ShouldContain("changed nothing");
    }

    [Fact]
    public async Task ModelCodeSetLocal_ADirectoryWithNoManifest_ExitsInvalidArgument()
    {
        var dir = TestData.CreateTempRoot("ai-raccoon-exit-no-manifest");
        try
        {
            var store = new FakeConfigStore();
            var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands(), codeEngine: store);

            var (exit, _, err) = await CliRun.RunAsync(["model", "code", "set", "local", dir], commands);

            exit.ShouldBe(ErrorCode.Usage.InvalidValue);
            err.ShouldContain("manifest.json");
        }
        finally
        {
            TestData.DeleteTempRoot(dir);
        }
    }

    [Fact]
    public async Task ModelSetOpenAi_DimsTheEndpointContradicts_ExitsInvalidArgument()
    {
        var store = new FakeConfigStore();
        var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands(new StubProbe(1024)),
            modelMigrations: store);

        var (exit, _, err) = await CliRun.RunAsync(
            ["model", "embedding", "set", "openai", "some-model", "--api-key", "k", "--dims", "768"], commands);

        exit.ShouldBe(ErrorCode.Usage.InvalidValue);
        err.ShouldContain("--dims 1024");
    }

    [Fact]
    public async Task ModelSetOpenAi_NoDimsForANon384Endpoint_ExitsInvalidArgument()
    {
        var store = new FakeConfigStore();
        var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands(new StubProbe(1024)),
            modelMigrations: store);

        var (exit, _, _) = await CliRun.RunAsync(
            ["model", "embedding", "set", "openai", "some-model", "--api-key", "k"], commands);

        exit.ShouldBe(ErrorCode.Usage.InvalidValue);
    }

    private static Task<(int Exit, string Out, string Err)> RunWithStoreThrowing(Exception toThrow) =>
        CliRun.RunAsync(["settings", "sweep", "show"],
            TestData.CreateConfigCommands(new ThrowingStore(toThrow), settings: new SettingsCommands()));

    private sealed class ThrowingStore(Exception toThrow) : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            throw toThrow;
    }

    private sealed class SettingsRoutedStore(ISettingsStore inner) : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.GetSettingAsync(key, cancellationToken);
    }

    private sealed class StubProbe(int dimensions) : IRemoteDimensionProbe
    {
        public Task<int> ProbeAsync(string model, string? baseUrl, string? apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(dimensions);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
