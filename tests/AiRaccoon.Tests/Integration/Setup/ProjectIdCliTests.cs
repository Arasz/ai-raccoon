using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     `project id register | get | check` as real <c>ai-raccoon</c> processes against the server
///     they auto-start: every row of the lookup-before-mint contract ai-badger builds against, plus
///     a server older than `/projects`. Each test gets its own data root and port.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ProjectIdCliTests : IAsyncLifetime
{
    private const string Registered = "0199a1b2-0000-7000-8000-000000000001";
    private const string Twin1 = "0199a1b2-0000-7000-8000-000000000002";
    private const string Twin2 = "0199a1b2-0000-7000-8000-000000000003";
    private const string Alias = "0199a1b2-0000-7000-8000-000000000004";
    private const string Dropped = "0199a1b2-0000-7000-8000-000000000005";
    private const string HeldGuid = "0199a1b2-0000-7000-8000-000000000006";
    private const string Fresh = "0199a1b2-0000-7000-8000-000000000007";
    private const string RawRegistered = "ai-badger";
    private const string HeldRaw = "legacy-held";
    private const string UnknownRaw = "never-seen";

    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-project-id-cli");
    private IAsyncDisposable? _env;
    private int _port;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every contract row: argv after `project id`, the exit, the exact stdout (empty for none), and a stderr fragment (null: stderr must be empty).</summary>
    public static TheoryData<string, string[], int, string, string?> ContractRows() => new()
    {
        { "register unknown guid", ["register", Fresh], 0, $"registered {Fresh}", null },
        { "register registered guid", ["register", Registered], 0, $"already registered {Registered}", null },
        { "register registered raw text", ["register", RawRegistered], 0, $"already registered {RawRegistered}", null },
        { "register alias", ["register", Alias], 0, $"already registered {Registered}", null },
        { "register row-holding guid", ["register", HeldGuid], 0, $"registered {HeldGuid}", null },
        { "register row-holding raw text", ["register", HeldRaw], 0, $"registered {HeldRaw}", null },
        { "register unknown raw text", ["register", UnknownRaw], ErrorCode.Usage.InvalidValue, "", "not a guid" },
        { "register retired", ["register", Dropped], ErrorCode.Usage.ProjectUnknown, "", "is retired" },
        { "get one", ["get", "--name", "repo-one"], 0, Registered, null },
        { "get raw text", ["get", "--name", RawRegistered], 0, RawRegistered, null },
        { "get none", ["get", "--name", "no-such-repo"], ErrorCode.Usage.ProjectUnknown, "", "no-such-repo" },
        { "get several", ["get", "--name", "repo-twin"], ErrorCode.Usage.ProjectAmbiguous, "", Twin2 },
        { "check registered guid", ["check", Registered], 0, $"known {Registered}", null },
        { "check registered raw text", ["check", RawRegistered], 0, $"known {RawRegistered}", null },
        { "check alias", ["check", Alias], 0, $"known {Registered}", null },
        { "check row-holding guid", ["check", HeldGuid], 0, $"known {HeldGuid}", null },
        { "check unknown guid", ["check", Fresh], ErrorCode.Usage.ProjectUnknown, $"unknown {Fresh}", null },
        { "check unknown raw text", ["check", UnknownRaw], ErrorCode.Usage.ProjectUnknown, $"unknown {UnknownRaw}", null },
        { "check retired", ["check", Dropped], ErrorCode.Usage.ProjectUnknown, $"retired {Dropped}", null }
    };

    public static TheoryData<string[]> BlankRows() => new()
    {
        new[] { "register", "" },
        new[] { "get", "--name", "" },
        new[] { "check", "" }
    };

    public async ValueTask InitializeAsync()
    {
        _env = await EnvScope.AcquireAsync(Ct, (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        _port = lease.Port;
        lease.ReleaseForBind();
        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, _port, CancellationToken.None);
        TestData.DeleteTempRoot(_dataRoot);
        if (_env is not null)
        {
            await _env.DisposeAsync();
        }
    }

    [RetryTheory]
    [MemberData(nameof(ContractRows))]
    public async Task EveryContractRow_OverARealServer(string label, string[] verb, int exit, string stdout, string? stderr)
    {
        var run = await RunAsync(["--quiet", "project", "id", .. verb]);

        run.ExitCode.ShouldBe(exit, $"'{label}': stdout '{run.Stdout}', stderr '{run.Stderr}'");
        run.Stdout.ShouldBe(stdout.Length == 0 ? "" : stdout + Environment.NewLine, $"'{label}': stdout is exactly the one contract line");
        if (stderr is null)
        {
            run.Stderr.ShouldBeEmpty($"'{label}': stderr is empty under --quiet");
        }
        else
        {
            run.Stderr.ShouldContain(stderr, Case.Sensitive, $"'{label}'");
        }
    }

    /// <summary>Ambiguity names every id on stderr, one per line after a header, and nothing on stdout.</summary>
    [RetryFact]
    public async Task GetSeveral_ListsEveryIdOnStderr()
    {
        var run = await RunAsync(["--quiet", "project", "id", "get", "--name", "repo-twin"]);

        run.ExitCode.ShouldBe(ErrorCode.Usage.ProjectAmbiguous);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1..].ShouldBe([Twin1, Twin2]);
    }

    /// <summary>A blank argument is refused before anything is dialled: no server is started on the port.</summary>
    [RetryTheory]
    [MemberData(nameof(BlankRows))]
    public async Task Blank_Exits10WithNoServerContacted(string[] verb)
    {
        var run = await RunAsync(["--quiet", "project", "id", .. verb]);

        run.ExitCode.ShouldBe(ErrorCode.Usage.InvalidValue, run.Stderr);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldStartWith("ai-raccoon: ");
        IsListening(_port).ShouldBeFalse("a blank argument must not start or reach a server");
        new McpTokenFile(_dataRoot).Read().ShouldBeNull("a blank argument must not reach the server that mints the token");
    }

    /// <summary>A proven server that predates `/projects` answers 404: the shared endpoint-missing exit, not "unknown".</summary>
    [RetryFact]
    public async Task OldServerWithoutProjects_Exits57()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        await new McpTokenFile(_dataRoot).EnsureAsync(Ct);
        var keyFile = new IdentityKeyFile(options);
        var signer = await keyFile.EnsureAsync(Ct) ?? throw new InvalidOperationException("the fixture could not mint its identity key");
        await using var oldServer = await FakeRaccoon.StartAsync(_port, HttpStatusCode.Unauthorized, Ct,
            proof: new FakeRaccoonProof { Signer = signer, RootFp = IdentityProof.RootFingerprint(keyFile.StateDirectory) });

        foreach (var verb in new[] { new[] { "check", Registered }, ["get", "--name", "repo-one"], ["register", Fresh] })
        {
            var run = await RunAsync(["--quiet", "project", "id", .. verb]);

            run.ExitCode.ShouldBe(ErrorCode.Server.EndpointMissing, $"'{string.Join(' ', verb)}': {run.Stderr}");
            run.Stdout.ShouldBeEmpty();
        }
    }

    /// <summary>
    ///     The cross-step chain: register, look up, read, set access and check the same id, while
    ///     reads and settings writes under an unknown id are refused.
    /// </summary>
    [RetryFact]
    public async Task RegisterThenSearchAndSettings_ChainOverOneServer()
    {
        (await RunAsync(["--quiet", "project", "id", "register", Fresh, "--name", "chain-repo"])).ExitCode.ShouldBe(0);
        (await RunAsync(["--quiet", "project", "id", "get", "--name", "chain-repo"])).Stdout.ShouldBe(Fresh + Environment.NewLine);

        await using (var client = await ConnectAsync())
        {
            var registered = await SearchAsync(client, Fresh);
            registered.IsError.ShouldNotBe(true, TextOf(registered));
            var unregistered = await SearchAsync(client, "0199a1b2-0000-7000-8000-0000000000ff");
            unregistered.IsError.ShouldBe(true);
            TextOf(unregistered).ShouldStartWith("project-not-registered:");
        }

        var set = await RunAsync(["--quiet", "settings", "access", "set", Fresh, "ro"]);
        set.ExitCode.ShouldBe(0, set.Stderr);

        var refused = await RunAsync(["--quiet", "settings", "access", "set", "0199a1b2-0000-7000-8000-0000000000ff", "ro"]);
        refused.ExitCode.ShouldBe(ErrorCode.Usage.ProjectUnknown, refused.Stderr);
        refused.Stdout.ShouldBeEmpty();

        var check = await RunAsync(["--quiet", "project", "id", "check", Fresh]);
        check.ExitCode.ShouldBe(0, check.Stderr);
        check.Stdout.ShouldBe("known " + Fresh + Environment.NewLine);
        check.Stderr.ShouldBeEmpty();
    }

    /// <summary>The settings half of the chain: a write under an id no project owns is refused with the project exit.</summary>
    [RetryFact]
    public async Task SettingsAccessSet_UnderAnUnregisteredId_Exits18()
    {
        var run = await RunAsync(["--quiet", "settings", "access", "set", "0199a1b2-0000-7000-8000-0000000000fe", "ro"]);

        run.ExitCode.ShouldBe(ErrorCode.Usage.ProjectUnknown, run.Stderr);
        run.Stdout.ShouldBeEmpty();
    }

    private Task<ProcessRun> RunAsync(string[] argv) =>
        RaccoonProcess.RunWithClosedInputAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), .. argv], HardCap, Ct);

    /// <summary>The registry, an alias, a retired id and two row-holding ids, all in place before any server warms its alias cache.</summary>
    private async Task SeedAsync()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(Ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (id, name) in new[] { (Registered, "repo-one"), (RawRegistered, RawRegistered), (Twin1, "repo-twin"), (Twin2, "repo-twin") })
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.InsertProject, new { id, name, createdAt = now }, cancellationToken: Ct));
        }

        foreach (var (hash, projectId) in new[] { ("held-guid", HeldGuid), ("held-raw", HeldRaw) })
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, value, project_id, scope, created_at, updated_at, embed_state)
                VALUES (@hash, 'held row', @projectId, 'project', 0, 0, 'pending')
                """, new { hash, projectId }, cancellationToken: Ct));
        }

        await ProjectIdAliases.PersistAppliedAsync(connection,
            new ProjectIdAliasMap([new ProjectIdAliasEntry(Alias, Registered)], [], [Dropped]), now, Ct);
    }

    private async Task<McpClient> ConnectAsync()
    {
        var token = new McpTokenFile(_dataRoot).Read() ?? throw new InvalidOperationException("no server minted a token for this data root");
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "project-id-cli-chain",
                Endpoint = new Uri($"http://127.0.0.1:{_port}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning)),
            true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<CallToolResult> SearchAsync(McpClient client, string projectId) =>
        await client.CallToolAsync("memory_search",
            new Dictionary<string, object?> { ["projectId"] = projectId, ["query"] = "anything", ["sessionId"] = "project-id-cli" },
            null, null, Ct);

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
