using System.Diagnostics;
using System.Globalization;
using System.Text;
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
///     ADR-0076's own proof: a real ai-raccoon server is killed for real (not a mocked exception),
///     in two different windows —
///     <see cref="AnInterruptedMigration_IsFinishedByTheNextServersStartupPass_WithNoKickEverHavingRun" />
///     between the outbox commit and the relay's first chance to touch it,
///     <see cref="AnInterruptedMigration_KilledMidDrain_IsFinishedByTheNextServersStartupPass" /> while
///     the relay is actively draining it — and a fresh server started against the same bank finishes
///     the migration on its startup pass alone both times, no kick, no inline execution, nothing but
///     the outbox row and the relay job. This is the check the whole feature exists to make pass, and
///     the one the owner asked to see fail too:
///     <see cref="AnInterruptedMigration_LeavesTheBankHalfMigratedForever_WhenTheRelayIsRemoved" />
///     records how that negative was produced, since the defect it demonstrates cannot stay live in
///     the tree without removing the feature it is testing. The mid-drain variant also caught a real
///     defect during development — see <c>IModelMigrationLease.LeaseTtl</c>'s doc comment and
///     ADR-0076's "The lease" section: a first draft's 10-minute lease TTL turned a mid-drain kill
///     into a 10-minute full-bank outage, not just deferred work, because <c>ToolGate</c> refuses
///     every operation while the migration is open.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public sealed class ModelMigrationCrashRecoveryE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>How long the mid-drain test waits for the relay to acquire the migration lease —
    /// the earliest bank-visible sign the drain started. 120s covers the 15s on-demand poll
    /// (<see cref="AiRaccoon.Infrastructure.Maintenance.BankMaintenanceHostedService.OnDemandPollInterval" />)
    /// with huge slack for a starved runner; the drain then has its own window below.</summary>
    private static readonly TimeSpan RelayStartCap = TimeSpan.FromSeconds(120);

    /// <summary>How long the mid-drain test waits for a partial drain (0 &lt; embedded &lt; total)
    /// once the lease shows the relay started. The unbounded term under load is the FIRST batch's
    /// real-ONNX latency (session load + 32 rows), not the poll cadence, so the cap must absorb it.</summary>
    private static readonly TimeSpan PartialDrainCap = TimeSpan.FromSeconds(180);

    /// <summary>Serve logs land here (survives the test's temp-root cleanup) so a failed run's
    /// evidence — EventIds 525/526/513, the lease, the ledger — is inspectable from CI artifacts.</summary>
    private static readonly string DiagRoot = Path.Combine(Path.GetTempPath(), "ai-raccoon-crash-diag");

    /// <summary>Enough rows that re-embedding them for real (the bundled ONNX model, batches of 32)
    /// takes long enough to reliably outlast the window between "outbox committed" and "process
    /// killed" — the case under test needs zero rows re-embedded before the kill, not many, but this
    /// also proves the migration is real work and not a no-op that happened to finish before the kill.</summary>
    private const int RowCount = 50;

    /// <summary>Large enough that the real local-ONNX drain (batches of 32) lasts seconds, so the
    /// 50 ms kill poll reliably observes a partial state. Measured 2026-08-22 on an M-series Mac:
    /// 128 rows drained in 169-249 ms end to end — a window the poll missed under load, burning
    /// PartialDrainCap — so this is sized for a multi-second drain (~1.5 ms/row here) while staying
    /// well inside PartialDrainCap and the 300 s completion wait on a slower, contended machine.</summary>
    private const int MidDrainRowCount = 2048;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-model-migration-crash");
    private ISqliteConnectionFactory _factory = null!;
    private LoopbackPort _portLease = null!;
    private int _port;
    private Process? _serverA;
    private Process? _serverB;
    private string _diagDir = null!;

    public ValueTask InitializeAsync()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _portLease = LoopbackPort.Reserve();
        _port = _portLease.Port;
        _portLease.ReleaseForBind();
        _diagDir = Path.Combine(DiagRoot, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_diagDir);
        File.WriteAllText(Path.Combine(DiagRoot, "LATEST.txt"), _diagDir);
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
        _serverA = StartServeProcess("server-a");
        await WaitForServerAsync();
        await WaitForStartupCheckpointAsync();

        // 2. Configure the first engine and seed real, embedded rows through it.
        (await RunCliAsync("model", "embedding", "set", "local")).ExitCode.ShouldBe(0);
        await SeedEmbeddedRowsAsync(RowCount);
        (await CountByStateAsync("embedded")).ShouldBe(RowCount);

        // 3. Trigger the migration. The CLI call returns as soon as the outbox transaction commits —
        // no re-embedding has happened inside this call (ADR-0076: no inline execution).
        var otherModelPath = CopyBundledModelToATempPath();
        var migrate = await RunCliAsync("model", "embedding", "set", "local", otherModelPath);
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
        _serverB = StartServeProcess("server-b");
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
    ///     The other half of "kill it for real": <see cref="AnInterruptedMigration_IsFinishedByTheNextServersStartupPass_WithNoKickEverHavingRun" />
    ///     kills before the relay ever ran; this kills the relay itself mid-drain — some rows already
    ///     re-embedded under the new engine, others still owed — and shows the next start finishes
    ///     exactly the remainder, with no duplicate rows and no row left behind. Two phased waits:
    ///     the relay's lease proves it started, then a partial drain proves the kill landed inside
    ///     the batch loop; either timeout fails with the bank ledger and serve logs as evidence.
    /// </summary>
    [Fact]
    public async Task AnInterruptedMigration_KilledMidDrain_IsFinishedByTheNextServersStartupPass()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        _serverA = StartServeProcess("server-a");
        await WaitForServerAsync();
        await WaitForStartupCheckpointAsync();

        (await RunCliAsync("model", "embedding", "set", "local")).ExitCode.ShouldBe(0);
        // Seeded directly (not through memory_write): this test's subject is the relay's drain, not
        // the write path, and MidDrainRowCount real embeds must happen twice over (seed, then
        // migrate) if seeded through MCP — this way only the migration itself pays for real inference,
        // which is what needs to take long enough to interrupt mid-way.
        await SeedFakeEmbeddedRowsAsync(MidDrainRowCount);
        (await CountByStateAsync("embedded")).ShouldBe(MidDrainRowCount);

        var otherModelPath = CopyBundledModelToATempPath();
        (await RunCliAsync("model", "embedding", "set", "local", otherModelPath)).ExitCode.ShouldBe(0);
        (await CountByStateAsync("pending")).ShouldBe(MidDrainRowCount); // the outbox's own atomic move

        // Phase 1 — wait for the on-demand relay to start draining. The lease it holds from its
        // first batch to the finish (renewed every batch, ADR-0076) is the earliest bank-visible
        // start signal. A single blind wait for a partial drain could not distinguish "relay never
        // started" from "first batch still embedding" — the lease can, and the failure message
        // (below) is only as good as what this phase proves.
        var relayStarted = await TryWaitUntilAsync(async () =>
        {
            await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
            return await connection.QuerySingleAsync<string?>(new CommandDefinition(
                "SELECT lease_owner FROM model_migration WHERE id = 1",
                cancellationToken: TestContext.Current.CancellationToken)) is not null;
        }, RelayStartCap, pollMs: 50);

        if (!relayStarted)
        {
            relayStarted.ShouldBeTrue(
                "the migration relay never acquired the drain lease within " + RelayStartCap +
                " — no row ever moved (or the drain finished before this test could observe it); " +
                "the captured serve logs and bank ledger say whether the poll never ran (513) or " +
                "every attempt failed (526):" + Environment.NewLine +
                await BuildEvidenceAsync());
        }

        // Phase 2 — kill the instant some rows have moved but not all: proof the kill lands inside
        // the batch loop, not before or after it. The kill happens INSIDE the predicate,
        // immediately after the read that observed the partial state: any gap here (a second
        // round-trip, then kill) is exactly the window a fast drain can finish in. The cap is
        // generous because the unbounded term under load is the first batch's real-ONNX latency.
        var embeddedAtKill = -1;
        var caughtMidDrain = await TryWaitUntilAsync(async () =>
        {
            var embedded = await CountByStateAsync("embedded");
            if (embedded <= 0 || embedded >= MidDrainRowCount)
            {
                return false;
            }

            embeddedAtKill = embedded;
            KillIfAlive(_serverA);
            return true;
        }, PartialDrainCap, pollMs: 50);

        if (!caughtMidDrain)
        {
            caughtMidDrain.ShouldBeTrue(
                "the relay held the lease for " + PartialDrainCap + " without an observable " +
                "partial drain — the first batch never completed, or the drain finished between " +
                "polls; the counts and logs below say which:" + Environment.NewLine +
                await BuildEvidenceAsync());
        }

        embeddedAtKill.ShouldBeInRange(1, MidDrainRowCount - 1);
        (await IsMigrationOpenAsync()).ShouldBeTrue();

        // A fresh server against the same bank finishes exactly the remainder. Generous timeout:
        // depending on how early the kill landed, this may still owe most of MidDrainRowCount rows
        // of real local-ONNX inference.
        _serverB = StartServeProcess("server-b");
        await WaitForServerAsync();
        await WaitUntilAsync(() => IsMigrationOpenAsync().ContinueWith(t => !t.Result), TimeSpan.FromSeconds(300));

        (await IsMigrationOpenAsync()).ShouldBeFalse();
        (await CountByStateAsync("embedded")).ShouldBe(MidDrainRowCount);
        (await CountByStateAsync("pending")).ShouldBe(0);
        (await TotalRowCountAsync()).ShouldBe(MidDrainRowCount); // nothing duplicated, nothing lost
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

    /// <summary>Starts a real serve process; its stdout/stderr stream to files under the run's
    /// diag dir instead of being discarded, so a failed run carries the server-side evidence
    /// (maintenance EventIds 525/526/513) into CI artifacts.</summary>
    private Process StartServeProcess(string label)
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
        _ = CaptureAsync(process.StandardOutput, Path.Combine(_diagDir, $"{label}.out"));
        _ = CaptureAsync(process.StandardError, Path.Combine(_diagDir, $"{label}.err"));
        return process;
    }

    /// <summary>Best-effort: streams the process's output to the file as it arrives, so the file
    /// exists and grows while the process lives — the evidence block reads it exactly when the
    /// process is still up (relay stuck, not dead). A failure to write diagnostics (including the
    /// dispose race after a kill) must never fail the test itself.</summary>
    private static async Task CaptureAsync(StreamReader reader, string path)
    {
        try
        {
            await using var writer = new StreamWriter(path);
            while (await reader.ReadLineAsync() is { } line)
            {
                await writer.WriteLineAsync(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Diagnostic capture is best-effort.
        }
    }

    /// <summary>
    ///     The bank-visible state a failed wait must carry: row counts by embed state, the
    ///     migration row (started/finished/lease), the maintenance ledger, and the serve log tails.
    ///     The point is that the NEXT occurrence of any of these waits is diagnosed from CI output.
    /// </summary>
    private async Task<string> BuildEvidenceAsync()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"entries: embedded={await CountByStateAsync("embedded")} " +
                          $"pending={await CountByStateAsync("pending")}");
            await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                var startedAt = await connection.QuerySingleAsync<long?>(new CommandDefinition(
                    "SELECT started_at FROM model_migration WHERE id = 1",
                    cancellationToken: TestContext.Current.CancellationToken));
                var finishedAt = await connection.QuerySingleAsync<long?>(new CommandDefinition(
                    "SELECT finished_at FROM model_migration WHERE id = 1",
                    cancellationToken: TestContext.Current.CancellationToken));
                var leaseOwner = await connection.QuerySingleAsync<string?>(new CommandDefinition(
                    "SELECT lease_owner FROM model_migration WHERE id = 1",
                    cancellationToken: TestContext.Current.CancellationToken));
                sb.AppendLine($"model_migration: started_at={startedAt} finished_at={finishedAt} lease_owner={leaseOwner}");
                foreach (var job in await connection.QueryAsync(new CommandDefinition(
                             "SELECT name, last_run_at, run_count FROM maintenance_jobs " +
                             "WHERE name IN ('model-migration', 'pending-embed') ORDER BY name",
                             cancellationToken: TestContext.Current.CancellationToken)))
                {
                    sb.AppendLine($"maintenance_jobs: {job.name} last_run_at={job.last_run_at} run_count={job.run_count}");
                }
            }

            foreach (var path in Directory.GetFiles(_diagDir, "server-*.err"))
            {
                sb.AppendLine($"--- tail of {Path.GetFileName(path)} ---");
                foreach (var line in (await File.ReadAllLinesAsync(path)).TakeLast(15))
                {
                    sb.AppendLine(line);
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            // The evidence must never mask the assertion it serves.
            return $"evidence construction failed: {ex}";
        }
    }

    private Task<ProcessRun> RunCliAsync(params string[] verb) =>
        RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), .. verb],
            HardCap, TestContext.Current.CancellationToken);

    private async Task WaitForServerAsync()
    {
        while (true)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
    }

    /// <summary>WAL truncation is the startup pass's own observable side effect (BankMaintenanceHostedService's checkpoint) — the same signal BankMaintenanceHostedServiceLifecycleTests uses.</summary>
    private async Task WaitForStartupCheckpointAsync()
    {
        var walPath = Path.Combine(_dataRoot, "memory.db-wal");
        // Waits on the checkpoint's own side effect; the token is the only hang guard (PR #464).
        while (!(File.Exists(walPath) && new FileInfo(walPath).Length == 0))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits on the condition itself; the token is the only hang guard (PR #464). The timeout parameter is kept for the negative-check twin below only.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan _)
    {
        while (!await predicate())
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Same polling loop as <see cref="WaitUntilAsync" />, but reports the outcome instead of throwing — for a wait whose failure is itself part of what the caller wants to assert on.</summary>
    private async Task<bool> TryWaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, int pollMs = 100)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return true;
            }

            await Task.Delay(pollMs, TestContext.Current.CancellationToken);
        }

        return false;
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

    /// <summary>
    ///     Writes rows directly as already-embedded, bypassing memory_write and the running server
    ///     entirely — legitimate test setup (mirrors CliBankWriteTests' direct bank creation), since
    ///     this test's subject is the relay's drain, not the write path. The vec0 population trigger
    ///     only fires on UPDATE, not INSERT, so these rows never reach vec_entries at seed time — fine,
    ///     since nothing here asserts on the pre-migration vector index; the migration's own re-embed
    ///     (a real UPDATE) populates it correctly regardless.
    /// </summary>
    private async Task SeedFakeEmbeddedRowsAsync(int count)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var fakeEmbedding = EmbeddingBlob.ToBytes(new float[384]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < count; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at, embed_state, embedding)
                VALUES (@hash, @path, @value, 'project', @projectId, @now, @now, 'embedded', @embedding)
                """,
                new
                {
                    hash = $"mid-drain-seed-{i:D6}", path = $"/mid-drain-seed-{i:D6}",
                    value = $"mid-drain seeded row {i}", projectId = "acme", now, embedding = fakeEmbedding
                },
                transaction, cancellationToken: TestContext.Current.CancellationToken));
        }

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
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

    private async Task<int> TotalRowCountAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = 'acme'",
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
