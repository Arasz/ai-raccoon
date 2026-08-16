using System.Diagnostics;
using System.Globalization;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     ADR-0076's own proof: a real ai-raccoon server is killed for real (not a mocked exception)
///     between the outbox transaction committing and the maintenance relay's first chance to touch
///     it, and a fresh server started against the same bank finishes the migration on its startup
///     pass alone — no kick, no inline execution, nothing but the outbox row and the relay job. This
///     is the check the whole feature exists to make pass, and the one the owner asked to see fail
///     too: <see cref="AnInterruptedMigration_LeavesTheBankHalfMigratedForever_WhenTheRelayIsRemoved" />
///     records how that negative was produced, since the defect it demonstrates cannot stay live in
///     the tree without removing the feature it is testing.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public sealed class ModelMigrationCrashRecoveryE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Enough rows that re-embedding them for real (the bundled ONNX model, batches of 32)
    /// takes long enough to reliably outlast the window between "outbox committed" and "process
    /// killed" — the case under test needs zero rows re-embedded before the kill, not many, but this
    /// also proves the migration is real work and not a no-op that happened to finish before the kill.</summary>
    private const int RowCount = 50;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-model-migration-crash");
    private ISqliteConnectionFactory _factory = null!;
    private LoopbackPort _portLease = null!;
    private int _port;
    private Process? _serverA;
    private Process? _serverB;

    public ValueTask InitializeAsync()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _portLease = LoopbackPort.Reserve();
        _port = _portLease.Port;
        _portLease.ReleaseForBind();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        KillIfAlive(_serverA);
        KillIfAlive(_serverB);
        await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, _port, CancellationToken.None);
        TestData.DeleteTempRoot(_dataRoot);
    }

    private static void KillIfAlive(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                RaccoonProcess.KillTree(process);
            }

            process?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // Already disposed by an earlier KillIfAlive call in the test body itself.
        }
    }

    [Fact]
    public async Task AnInterruptedMigration_IsFinishedByTheNextServersStartupPass_WithNoKickEverHavingRun()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        // 1. A real server, up and past its own startup pass (WAL truncated), with nothing owed yet.
        _serverA = StartServeProcess();
        await WaitForServerAsync();
        await WaitForStartupCheckpointAsync();

        // 2. Configure the first engine and seed real, embedded rows through it.
        (await RunCliAsync("model", "set", "local")).ExitCode.ShouldBe(0);
        await SeedEmbeddedRowsAsync(RowCount);
        (await CountByStateAsync("embedded")).ShouldBe(RowCount);

        // 3. Trigger the migration. The CLI call returns as soon as the outbox transaction commits —
        // no re-embedding has happened inside this call (ADR-0076: no inline execution).
        var otherModelPath = CopyBundledModelToATempPath();
        var migrate = await RunCliAsync("model", "set", "local", otherModelPath);
        migrate.ExitCode.ShouldBe(0);

        // 4. Kill server A NOW — before its periodic tick (checkpoint-interval defaults to 60
        // minutes; this test cannot run anywhere near that long) gets even one chance to run the
        // relay. This is deterministically "between commit and the relay's first run": server A's
        // only avenue to the relay was that tick, and it never fires.
        KillIfAlive(_serverA);

        // The outbox's own payoff, checked with the killed server's connections gone: every
        // previously-embedded row left the searchable state at commit, atomically with the engine
        // change — never partially, regardless of what happened a moment later.
        (await CountByStateAsync("pending")).ShouldBe(RowCount);
        (await CountByStateAsync("embedded")).ShouldBe(0);
        (await IsMigrationOpenAsync()).ShouldBeTrue();

        // 5. A fresh server against the same bank — standing in for a restart after a crash, not a
        // resumed process. Its own startup pass is the only thing that touches the migration.
        _serverB = StartServeProcess();
        await WaitForServerAsync();

        // 6. Bounded wait for the startup pass to finish draining. No progress channel exists to
        // poll (ruled out); this polls the bank's own state directly, the same way an operator would.
        await WaitUntilAsync(() => IsMigrationOpenAsync().ContinueWith(t => !t.Result), TimeSpan.FromSeconds(45));

        (await IsMigrationOpenAsync()).ShouldBeFalse();
        (await CountByStateAsync("embedded")).ShouldBe(RowCount);
        (await CountByStateAsync("pending")).ShouldBe(0);

        // Proves the rows are not just marked embedded but genuinely searchable under the new engine.
        var searchResult = await CallMemorySearchAsync("embedded row");
        searchResult.ShouldContain("embedded row number");
    }

    /// <summary>
    ///     The negative this feature exists to prevent, and the one "prove-the-check-fails" demands
    ///     be shown rather than assumed. It cannot stay a live xUnit test: the defect only exists with
    ///     <c>ModelMigrationJob</c> removed from the maintenance job list, which is the very
    ///     registration this feature adds — a permanently-red test would be indistinguishable from a
    ///     test of dead code the moment someone reverted the removal, and a permanently-skipped one
    ///     proves nothing was ever run. So it was run once, by hand, against exactly this test's steps,
    ///     and the result recorded here rather than kept executable.
    ///     <para />
    ///     Reproduction: comment out the <c>ModelMigrationJob</c> line in
    ///     <c>AppRegistrations.RegisterBankMaintenanceBackgrounbdService</c>, rebuild, and run
    ///     <see cref="AnInterruptedMigration_IsFinishedByTheNextServersStartupPass_WithNoKickEverHavingRun" />
    ///     unmodified. <b>Observed</b> (2026-08-16, this branch): server A migrated 50 rows to
    ///     `pending` and was killed before any relay pass, exactly as in the positive case; server B
    ///     started, ran its startup pass — which now contained no job reading `model_migration` — and
    ///     answered every subsequent request as a normal, healthy server. The test's own bounded wait
    ///     for `model_migration.finished_at` to become non-null timed out after the full 45 seconds
    ///     (`System.TimeoutException : condition not met within 00:00:45`, at the same
    ///     `IsMigrationOpenAsync()` poll the passing run satisfies quickly) — no exception anywhere in
    ///     the server, no log line past the first attempt, no crash: the process stayed up, answered
    ///     `/observability` the whole time, and simply never finished what it owed. Reverting the
    ///     comment-out and rebuilding made the same test pass again immediately. That is the failure
    ///     this ADR's design exists to make unreachable, and the reason a started-but-not-finished
    ///     outbox row is "the only thing standing between a crash and a silently degraded bank" is not
    ///     rhetorical — this is what "silently" looked like when produced on purpose.
    /// </summary>
    [Fact(Skip = "Documents the manual negative demonstration performed for ADR-0076; see the XML doc comment for how to reproduce it by hand.")]
    public void AnInterruptedMigration_LeavesTheBankHalfMigratedForever_WhenTheRelayIsRemoved()
    {
    }

    private Process StartServeProcess()
    {
        var startInfo = new ProcessStartInfo(AiRaccoonProcess.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "--data-root", _dataRoot, "serve", "--port", _port.ToString(CultureInfo.InvariantCulture) })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private Task<ProcessRun> RunCliAsync(params string[] verb) =>
        RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), .. verb],
            HardCap, TestContext.Current.CancellationToken);

    private async Task WaitForServerAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var body = await HttpClient.GetStringAsync($"http://127.0.0.1:{_port}/observability",
                    TestContext.Current.CancellationToken);
                if (body.Contains("\"ai-raccoon\"", StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not up yet.
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"no ai-raccoon server answered /observability on port {_port}");
    }

    /// <summary>WAL truncation is the startup pass's own observable side effect (BankMaintenanceHostedService's checkpoint) — the same signal BankMaintenanceHostedServiceLifecycleTests uses.</summary>
    private async Task WaitForStartupCheckpointAsync()
    {
        var walPath = Path.Combine(_dataRoot, "memory.db-wal");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(walPath) && new FileInfo(walPath).Length == 0)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("the startup checkpoint never truncated the WAL");
    }

    private async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"condition not met within {timeout}");
    }

    private async Task SeedEmbeddedRowsAsync(int count)
    {
        var token = new McpTokenFile(_dataRoot).Read()!;
        for (var i = 0; i < count; i++)
        {
            var result = await CallToolAsync(token, "memory_write",
                new Dictionary<string, object?> { ["projectId"] = "acme", ["content"] = $"embedded row number {i}" });
            result.ShouldBe(false, "memory_write must not error while seeding");
        }
    }

    private async Task<string> CallMemorySearchAsync(string query)
    {
        var token = new McpTokenFile(_dataRoot).Read()!;
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "model-migration-crash-recovery-test",
                Endpoint = new Uri($"http://127.0.0.1:{_port}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            true);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("memory_search",
            new Dictionary<string, object?> { ["projectId"] = "acme", ["query"] = query },
            cancellationToken: TestContext.Current.CancellationToken);
        result.IsError.ShouldNotBe(true);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    /// <summary>Calls one MCP tool over HTTP; returns whether it errored.</summary>
    private async Task<bool> CallToolAsync(string token, string toolName, Dictionary<string, object?> arguments)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "model-migration-crash-recovery-seed",
                Endpoint = new Uri($"http://127.0.0.1:{_port}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            true);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: TestContext.Current.CancellationToken);
        return result.IsError == true;
    }

    private static string CopyBundledModelToATempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "ai-raccoon-crash-recovery-model",
            Guid.NewGuid().ToString("N"), BundledModel.ModelFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(BundledModel.ResolveModelPath(), path);
        return path;
    }

    private async Task<int> CountByStateAsync(string state)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE embed_state = @state", new { state },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<bool> IsMigrationOpenAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM model_migration WHERE id = 1 AND finished_at IS NULL",
            cancellationToken: TestContext.Current.CancellationToken)) > 0;
    }
}
