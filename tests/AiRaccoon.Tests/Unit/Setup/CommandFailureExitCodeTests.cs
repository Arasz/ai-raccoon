using System.Net;
using System.Net.Sockets;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     What a command's failure exits with once argv has parsed: the catalog case the failure names
///     (ADR-0107), Ctrl-C is SIGC, and anything unnamed is Internal.Unexpected, never "you mistyped".
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CommandFailureExitCodeTests
{
    [Fact]
    public async Task AnUnexpectedFault_ExitsUnexpected_NotInvalidValue()
    {
        var (exit, _, err) = await RunWithStoreThrowing(new InvalidOperationException("boom"));

        exit.ShouldBe(ErrorCode.Internal.Unexpected);
        err.ShouldContain("boom");
    }

    /// <summary>A cancellation the caller never asked for is a timeout (an HttpClient timeout surfaces this way).</summary>
    [Fact]
    public async Task ACancellationTheCallerDidNotAskFor_ExitsTimeout()
    {
        var (exit, _, _) = await RunWithStoreThrowing(new TaskCanceledException("timed out"));

        exit.ShouldBe(ErrorCode.Internal.Timeout);
    }

    [Theory]
    [InlineData(5, ErrorCode.Bank.Busy)]
    [InlineData(6, ErrorCode.Bank.Busy)]
    [InlineData(26, ErrorCode.Bank.Corrupted)]
    [InlineData(14, ErrorCode.Bank.OpenFailed)]
    [InlineData(10, ErrorCode.Bank.OpenFailed)]
    public async Task ASqliteFailure_ExitsTheBankCaseItsErrorCodeNames(int sqliteErrorCode, int expected)
    {
        var (exit, _, err) = await RunWithStoreThrowing(new SqliteException("sqlite said no", sqliteErrorCode));

        exit.ShouldBe(expected);
        err.ShouldContain("sqlite said no");
    }

    [Theory]
    [InlineData("wrong-key", ErrorCode.Key.WrongKey)]
    [InlineData("legacy-key", ErrorCode.Key.LegacyKeyDerivation)]
    [InlineData("bws-missing", ErrorCode.Key.BwsNotInstalled)]
    [InlineData("bws-timeout", ErrorCode.Key.BwsTimedOut)]
    [InlineData("bws-failed", ErrorCode.Key.BwsFailed)]
    [InlineData("secret-not-a-key", ErrorCode.Key.SecretNotAKey)]
    [InlineData("sidecar", ErrorCode.Key.SourceSidecarInvalid)]
    [InlineData("argument", ErrorCode.Usage.InvalidValue)]
    [InlineData("format", ErrorCode.Usage.InvalidValue)]
    [InlineData("undialable-port", ErrorCode.Usage.UndialablePort)]
    [InlineData("migration-open", ErrorCode.Server.MigrationRefused)]
    [InlineData("permission", ErrorCode.Environment.PermissionDenied)]
    [InlineData("read-only", ErrorCode.Environment.ReadOnlyDataRoot)]
    [InlineData("too-long", ErrorCode.Environment.PathTooLong)]
    [InlineData("io", ErrorCode.Environment.IoFailed)]
    [InlineData("bind-denied", ErrorCode.Environment.BindDenied)]
    [InlineData("unhandled-command", ErrorCode.Internal.UnhandledCommand)]
    public async Task ATypedFailure_ExitsTheCaseItNames(string failure, int expected)
    {
        var (exit, _, _) = await RunWithStoreThrowing(Failure(failure));

        exit.ShouldBe(expected);
    }

    /// <summary>The settings server's status is the verdict: each refusal a script can act on has its own code.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ErrorCode.Usage.RequestRejected)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCode.Server.RequestTokenRefused)]
    [InlineData(HttpStatusCode.Forbidden, ErrorCode.Internal.UnusableResponse)]
    [InlineData(HttpStatusCode.NotFound, ErrorCode.Server.EndpointMissing)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCode.Internal.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorCode.Internal.ServerError)]
    public async Task AServerStatus_ExitsTheCaseItNames(HttpStatusCode status, int expected)
    {
        var (exit, _, _) = await CliRun.RunAsync(["noise", "entries"],
            TestData.CreateConfigCommands(new FakeMemoryStore(), noiseEntries: new NoiseEntriesCommands(RespondingWith(status))));

        exit.ShouldBe(expected);
    }

    /// <summary>A model reset while a migration is open is refused with 409.</summary>
    [Fact]
    public async Task AServer409OnAModelReset_ExitsMigrationRefused()
    {
        var (exit, _, _) = await CliRun.RunAsync(["settings", "model", "reset"],
            TestData.CreateConfigCommands(new SettingsRoutedStore(RespondingWith(HttpStatusCode.Conflict)), settings: new SettingsCommands()));

        exit.ShouldBe(ErrorCode.Server.MigrationRefused);
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
    public async Task ModelCodeSetLocal_ADirectoryWithNoManifest_ExitsManifestRejected()
    {
        var dir = TestData.CreateTempRoot("ai-raccoon-exit-no-manifest");
        try
        {
            var store = new FakeConfigStore();
            var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands(), codeEngine: store);

            var (exit, _, err) = await CliRun.RunAsync(["model", "code", "set", "local", dir], commands);

            exit.ShouldBe(ErrorCode.Model.ManifestRejected);
            err.ShouldContain("manifest.json");
        }
        finally
        {
            TestData.DeleteTempRoot(dir);
        }
    }

    [Fact]
    public async Task ModelSetOpenAi_DimsTheEndpointContradicts_ExitsDimensionMismatch()
    {
        var store = new FakeConfigStore();
        var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands(new StubProbe(1024)),
            modelMigrations: store);

        var (exit, _, err) = await CliRun.RunAsync(
            ["model", "embedding", "set", "openai", "some-model", "--api-key", "k", "--dims", "768"], commands);

        exit.ShouldBe(ErrorCode.Model.DimensionMismatch);
        err.ShouldContain("--dims 1024");
    }

    [Fact]
    public async Task ModelSetOpenAi_NoDimsForANon384Endpoint_ExitsDimensionMismatch()
    {
        var store = new FakeConfigStore();
        var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands(new StubProbe(1024)),
            modelMigrations: store);

        var (exit, _, _) = await CliRun.RunAsync(
            ["model", "embedding", "set", "openai", "some-model", "--api-key", "k"], commands);

        exit.ShouldBe(ErrorCode.Model.DimensionMismatch);
    }

    [Fact]
    public async Task ModelSetOpenAi_AnEndpointThatCannotBeReached_ExitsEndpointUnreachable()
    {
        var store = new FakeConfigStore();
        var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands(new FailingProbe()),
            modelMigrations: store);

        var (exit, _, err) = await CliRun.RunAsync(
            ["model", "embedding", "set", "openai", "some-model", "--api-key", "k"], commands);

        exit.ShouldBe(ErrorCode.Model.EndpointUnreachable);
        err.ShouldContain("could not be reached");
    }

    private static Exception Failure(string name) =>
        name switch
        {
            "wrong-key" => new BankKeyMismatchException("the key does not open the bank"),
            "legacy-key" => new BankKeyMismatchException("still under the old derivation", legacyDerivation: true),
            "bws-missing" => new BwsInvocationException(BwsFailure.NotInstalled, "bws not found"),
            "bws-timeout" => new BwsInvocationException(BwsFailure.TimedOut, "bws timed out after 15s"),
            "bws-failed" => new BwsInvocationException(BwsFailure.Failed, "bws failed (exit 1): no access"),
            "secret-not-a-key" => new UnsupportedKeyTypeException(),
            "sidecar" => new EncryptionSourceException("encryption source sidecar 'x' is corrupt: bad json"),
            "argument" => new ArgumentException("bad key"),
            "format" => new FormatException("not a number"),
            "undialable-port" => new UndialablePortException("cannot dial --port 0"),
            "migration-open" => new ModelMigrationInProgressException("ai-raccoon: model migration in progress"),
            "permission" => new UnauthorizedAccessException("denied"),
            "read-only" => new IOException("Read-only file system : '/x'"),
            "too-long" => new PathTooLongException("the path is too long"),
            "io" => new IOException("disk went away"),
            "bind-denied" => new IOException("Failed to bind to address", new SocketException((int)SocketError.AccessDenied)),
            "unhandled-command" => new UnhandledCommandException("unhandled command: x"),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
        };

    private static ServerSettingsStore RespondingWith(HttpStatusCode status) =>
        new(new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))))
            {
                BaseAddress = new Uri("http://127.0.0.1:1/")
            },
            "test-token");

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

        public override Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.DeleteSettingAsync(key, cancellationToken);
    }

    private sealed class FailingProbe : IRemoteDimensionProbe
    {
        public Task<int> ProbeAsync(string model, string? baseUrl, string? apiKey, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
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
