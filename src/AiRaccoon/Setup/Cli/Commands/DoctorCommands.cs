using System.Diagnostics;
using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Embedding.NeuralEngine;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Setup.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `doctor`: verifies the bank's schema shape and reports — it never repairs. Reads
///     the bank read-only, exactly like <c>OpenSnapshotReadOnly</c> (AppRegistrations.cs), so it
///     never runs <c>MemorySchema.EnsureAsync</c> against the very bank it is inspecting.
/// </summary>
public sealed partial class DoctorCommands(ISqliteConnectionFactory bankConnectionFactory, IEncryptionKeyResolver keyResolver,
    InfrastructureOptions options, ILogger<DoctorCommands> logger)
{
    public async Task<int> RunAsync(StandardStreams streams, CancellationToken cancellationToken)
    {
        var bankPath = bankConnectionFactory.BankPath;
        if (!File.Exists(bankPath))
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: doctor: no bank at {bankPath}");
            return ErrorCode.Bank.NoBank;
        }

        ResolvedKey resolvedKey;
        try
        {
            resolvedKey = await keyResolver.ResolveAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.FailedToResolveEncryptionKey(logger, ex);
            await streams.WriteErrorLineAsync($"ai-raccoon: doctor: could not resolve the encryption key: {ex.Message}");
            return CliFailureErrorCode.For(ex, ErrorCode.Key.Unresolved);
        }

        SqliteConnection connection;
        try
        {
            connection = await OpenBankReadOnlyAsync(bankPath, resolvedKey.Passphrase, cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == NotADatabaseErrorCode)
        {
            // Microsoft.Data.Sqlite's "file is not a database" verdict can surface here (a probe
            // inside the read-only open itself) or, for a bank that opens but fails its first real
            // statement, from DiagnoseAsync below — both catch sites report the same way.
            return await ReportNotADatabaseAsync(bankPath, resolvedKey, ex, streams);
        }
        catch (SqliteException ex)
        {
            Log.FailedToOpenBank(logger, bankPath, ex);
            await streams.WriteErrorLineAsync($"ai-raccoon: doctor: could not open the bank read-only: {ex.Message}");
            return CliFailureErrorCode.For(ex);
        }

        await using (connection)
        {
            SchemaDoctorReport report;
            try
            {
                report = await SchemaDoctor.DiagnoseAsync(connection, cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == NotADatabaseErrorCode)
            {
                return await ReportNotADatabaseAsync(bankPath, resolvedKey, ex, streams);
            }

            var engines = new Dictionary<CorpusEngineProbe, CorpusEngineState>();
            foreach (var probe in CorpusEngineProbe.All)
            {
                engines[probe] = await ReadCorpusEngineStateAsync(connection, probe, cancellationToken);
            }

            var threads = await ReadEmbeddingThreadsStateAsync(connection, cancellationToken);
            var migration = await ReadModelMigrationStateAsync(connection, cancellationToken);
            var coreMl = await ReadCoreMlLineAsync(connection, cancellationToken);
            return await ReportAsync(bankPath, report, engines, threads, coreMl, migration, streams);
        }
    }

    /// <summary>
    ///     SQLITE_NOTADB is what a wrong encryption key looks like too (ADR-0107 PC.0). When the
    ///     ADR-0111 key-check sidecar exists and can be trusted, doctor reports the confident
    ///     verdict it proves — right key means the bank file itself is corrupt; wrong key means the
    ///     resolved key is wrong. A missing or unusable sidecar keeps naming both causes and both
    ///     remedies, as before — so does a verifier that confirms the key while a rekey marker
    ///     (ADR-0111 D6) is present, since the sidecar may not have caught up with an in-progress
    ///     rekey yet. Read-only throughout — doctor never mints, rewrites, or clears either file.
    /// </summary>
    private async Task<int> ReportNotADatabaseAsync(string bankPath, ResolvedKey resolvedKey, SqliteException ex,
        StandardStreams streams)
    {
        Log.BankIsNotADatabase(logger, bankPath, ex);

        var verifier = TryReadKeyCheck(bankPath);
        if (verifier is not null && resolvedKey.Passphrase is not null)
        {
            if (!KeyCheckSidecar.Verifies(verifier, resolvedKey.Passphrase))
            {
                await streams.WriteErrorLineAsync(
                    $"ai-raccoon: doctor: the bank at {bankPath} exists but is not a SQLite database (SQLite error {ex.SqliteErrorCode}); " +
                    "its key verifier confirms the resolved encryption key is wrong — check the encryption key source");
                return ErrorCode.Key.WrongKey;
            }

            // The verifier confirms this key, which normally proves the bank itself is damaged.
            // But a rekey in progress (ADR-0111 D6, issue #705) may not have rewritten it for its
            // new key yet — this could be exactly that window, not a truly corrupt file — so fall
            // through to the ambiguous, both-causes verdict below instead of confidently naming
            // corruption. Read-only: doctor never mints, rewrites, or clears the marker itself.
            if (!new RekeyMarker(bankPath).IsPresent)
            {
                await streams.WriteErrorLineAsync(
                    $"ai-raccoon: doctor: the bank at {bankPath} exists but is not a SQLite database (SQLite error {ex.SqliteErrorCode}); " +
                    "its key verifier confirms the encryption key is right, so the bank file itself is corrupt — restore it from a backup");
                return ErrorCode.Bank.Corrupted;
            }
        }

        await streams.WriteErrorLineAsync(
            $"ai-raccoon: doctor: the bank at {bankPath} exists but is not a SQLite database (SQLite error {ex.SqliteErrorCode}); " +
            "wrong key or corrupt bank — check the encryption key source if this data root was working before, " +
            "or restore the bank from a backup if the file itself is damaged");
        return ErrorCode.Bank.Corrupted;
    }

    /// <summary>Read-only, never a mint or a rewrite; an unreadable or shared sidecar is treated as absent.</summary>
    private static KeyCheckRecord? TryReadKeyCheck(string bankPath)
    {
        try
        {
            return new KeyCheckSidecar(bankPath).Read();
        }
        catch (KeyCheckViolation)
        {
            return null;
        }
    }

    private static async Task<int> ReportAsync(string bankPath, SchemaDoctorReport report,
        IReadOnlyDictionary<CorpusEngineProbe, CorpusEngineState> engines,
        EmbeddingThreadsState threads, string? coreMl, MigrationState migration, StandardStreams streams)
    {
        await streams.WriteOutputLineAsync($"ai-raccoon doctor: {bankPath}");
        await streams.WriteOutputLineAsync($"user_version: {report.StoredVersion} (this binary: {report.CurrentVersion})");
        await streams.WriteOutputLineAsync($"application_id: {report.StoredDigest} (expected: {report.ExpectedDigest})");
        foreach (var probe in CorpusEngineProbe.All)
        {
            await streams.WriteOutputLineAsync(CorpusEngineLines.EngineLine(probe, engines[probe]));
        }

        // #522: what `embedding.threads` resolves to, via EmbeddingService's own resolver.
        await streams.WriteOutputLineAsync(
            $"embedding threads: {EmbeddingService.ThreadCountDisplay(threads.Threads)} ({threads.Source})");
        if (coreMl is not null)
        {
            await streams.WriteOutputLineAsync(coreMl);
        }

        foreach (var probe in CorpusEngineProbe.All)
        {
            await streams.WriteOutputLineAsync(CorpusEngineLines.PendingLine(probe, engines[probe]));
        }

        await streams.WriteOutputLineAsync(MigrationLine(migration));
        await streams.WriteOutputLineAsync("doctor verifies schema shape only; it never repairs a bank");

        switch (report.Status)
        {
            case SchemaDoctorStatus.VersionAheadOfBinary:
                await streams.WriteOutputLineAsync(
                    $"status: SCHEMA NEWER THAN THIS BINARY (bank is v{report.StoredVersion}, this binary supports up to v{report.CurrentVersion}) — update ai-raccoon");
                return ErrorCode.Bank.SchemaNewerThanBinary;

            case SchemaDoctorStatus.Healthy:
                // P1 §3 Decisions C/D/E: 24 is emitted only on a positively-read open row — a
                // guard-tripped read stays HEALTHY — and the schema verdicts (19/20) outrank it.
                if (migration.Result == MigrationRead.Open)
                {
                    await streams.WriteOutputLineAsync(
                        "status: MIGRATION IN PROGRESS (schema shape is healthy; MCP tool calls are refused until the re-embed finishes)");
                    return ErrorCode.Bank.MigrationOpen;
                }

                await streams.WriteOutputLineAsync("status: HEALTHY");
                return ErrorCode.Ok.Success;

            default:
                await streams.WriteOutputLineAsync($"status: SHAPE MISMATCH ({report.Findings.Count} finding(s))");
                foreach (var finding in report.Findings)
                {
                    await streams.WriteOutputLineAsync($"  - {finding.ObjectName}: {finding.Detail}");
                }

                await streams.WriteOutputLineAsync(
                    "remedy: start the server (ai-raccoon serve) — it repairs the schema on every open");
                return ErrorCode.Bank.SchemaMismatch;
        }
    }

    /// <summary>
    ///     One corpus's engine state (#422 for code, this task for memory). A shape-mismatched bank is
    ///     exactly the bank doctor exists to diagnose, so this extra read must never be what decides the
    ///     exit code: every table it touches may be missing or the wrong shape. Guarded by existence and
    ///     by catch, and the report says so. <see cref="CorpusEngineState.Unreadable" /> is the degraded
    ///     arm — never the false "not configured" remedy (P1 §1.3).
    /// </summary>
    private static async Task<CorpusEngineState> ReadCorpusEngineStateAsync(SqliteConnection connection,
        CorpusEngineProbe probe, CancellationToken cancellationToken)
    {
        try
        {
            var settingsExist = await TableExistsAsync(connection, "settings", cancellationToken);
            var provider = probe.ProviderKey is null || !settingsExist
                ? null
                : await ReadSettingAsync(connection, probe.ProviderKey, cancellationToken);
            var model = !settingsExist
                ? null
                : await ReadSettingAsync(connection, probe.ModelKey, cancellationToken);
            var baseUrl = probe.BaseUrlKey is null || !settingsExist
                ? null
                : await ReadSettingAsync(connection, probe.BaseUrlKey, cancellationToken);
            // Presence only, never the value: embedding.apiKey is a persisted secret (R1 S5).
            var apiKeySet = probe.ApiKeyKey is null || !settingsExist
                ? true
                : await SettingExistsAsync(connection, probe.ApiKeyKey, cancellationToken);

            var pending = await CountPendingRowsAsync(connection, probe, cancellationToken);

            // P1 §1.3 / R1 S6: "not configured" is reserved for a positively absent settings row —
            // a missing settings table is the unreadable arm; the pending count still reads off
            // its own table, so it survives on the same state (R2 N4).
            if (!settingsExist)
            {
                return new CorpusEngineState(null, null, null, pending, Unreadable: true);
            }

            return probe.ProviderKey is null
                ? string.IsNullOrWhiteSpace(model)
                    ? new CorpusEngineState(null, null, null, pending)
                    : DescribeLocalModel(model, pending)
                : string.IsNullOrWhiteSpace(provider)
                    ? new CorpusEngineState(null, null, null, pending)
                    : DescribeMemoryEngine(provider, model, baseUrl, apiKeySet, pending);
        }
        catch (SqliteException)
        {
            return new CorpusEngineState(null, null, null, null, Unreadable: true);
        }
    }

    /// <summary>A local model value: a manifest directory, a legacy .onnx file, or a missing path — only a directory gets the manifest read (P1 §1.3).</summary>
    private static CorpusEngineState DescribeLocalModel(string model, long? pending) =>
        Directory.Exists(model)
            ? new CorpusEngineState(ModelNameFor(model), model, null, pending)
            : new CorpusEngineState(model, null, null, pending);

    /// <summary>The memory engine's four arms (P1 §2.3): bundled when no model is set, openai:&lt;model&gt; when remote, else the local directory/file form.</summary>
    private static CorpusEngineState DescribeMemoryEngine(string provider, string? model, string? baseUrl,
        bool apiKeySet, long? pending)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return new CorpusEngineState("bundled", null, null, pending);
        }

        if (provider == "openai")
        {
            return new CorpusEngineState($"openai:{model}",
                string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl,
                apiKeySet ? null : EmbeddingEngineSetup.NoApiKeyRemedy,
                pending);
        }

        return DescribeLocalModel(model, pending);
    }

    /// <summary>#522: what `embedding.threads` currently resolves to, and why (an explicit setting vs the halved-core default).</summary>
    private static async Task<EmbeddingThreadsState> ReadEmbeddingThreadsStateAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var raw = await TableExistsAsync(connection, "settings", cancellationToken)
                ? await ReadSettingAsync(connection, EmbeddingSettingsKeys.Threads, cancellationToken)
                : null;
            var (threads, source) = EmbeddingService.ResolveThreadCountForDisplay(raw);
            return new EmbeddingThreadsState(threads, source);
        }
        catch (SqliteException)
        {
            var (threads, source) = EmbeddingService.ResolveThreadCountForDisplay(null);
            return new EmbeddingThreadsState(threads, source);
        }
    }

    /// <summary>
    ///     ADR-0118: the Neural Engine compile's last recorded state and the cache size, under
    ///     <c>device coreml</c> or whenever a cache exists; null otherwise. Never fails the report.
    /// </summary>
    private async Task<string?> ReadCoreMlLineAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var root = Path.Combine(options.DataRoot, EmbeddingService.CoreMlCacheDirectoryName);
        if (!Directory.Exists(root) && await ReadDeviceAsync(connection, cancellationToken) != EmbeddingDevice.CoreMl)
        {
            return null;
        }

        var cache = string.Create(CultureInfo.InvariantCulture, $"cache {CoreMlCache.SizeBytes(root) / (1024.0 * 1024.0):0} MiB");
        CoreMlStatus? status;
        try
        {
            status = CoreMlCache.ReadStatus(root);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"coreml: status.json unreadable, {cache}";
        }

        if (status is null)
        {
            return $"coreml: not started, {cache}";
        }

        var reason = string.IsNullOrEmpty(status.Reason) ? "" : $": {status.Reason}";
        var running = IsRunning(status.Pid) ? "" : " (not running)";
        return $"coreml: {status.State} ({status.Trigger}{reason}) at {FormatTimestamp(status.At.ToUnixTimeSeconds())}, " +
               $"pid {status.Pid}{running}, {cache}";
    }

    private static async Task<EmbeddingDevice> ReadDeviceAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return EmbeddingDeviceSetting.Parse(await TableExistsAsync(connection, "settings", cancellationToken)
                ? await ReadSettingAsync(connection, EmbeddingSettingsKeys.Device, cancellationToken)
                : null);
        }
        catch (SqliteException)
        {
            return EmbeddingDevice.Auto;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<long?> CountPendingRowsAsync(SqliteConnection connection,
        CorpusEngineProbe probe, CancellationToken cancellationToken) =>
        await TableExistsAsync(connection, probe.PendingTable, cancellationToken)
            ? await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                probe.PendingSql, cancellationToken: cancellationToken))
            : 0;

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: cancellationToken)) > 0;

    private static async Task<bool> SettingExistsAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM settings WHERE key = @key)", new { key }, cancellationToken: cancellationToken)) > 0;

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            MemorySql.SelectSetting, new { key }, cancellationToken: cancellationToken));

    /// <summary>The manifest's own model name when it is still readable, else the directory's leaf —
    /// doctor reports, so an unreadable manifest must not stop it printing the rest.</summary>
    private static string ModelNameFor(string directory)
    {
        try
        {
            return new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
                .Load(directory).Model;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"{Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar))} (manifest unreadable)";
        }
    }

    /// <summary>Mirrors AppRegistrations.OpenSnapshotReadOnly, aimed at the live bank instead of a sync snapshot: open, enable extensions, load vec0 — never EnsureAsync.</summary>
    private static async Task<SqliteConnection> OpenBankReadOnlyAsync(string bankPath, string? passphrase, CancellationToken cancellationToken)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = bankPath,
            Mode = SqliteOpenMode.ReadOnly
        };
        if (passphrase is not null)
        {
            csb.Password = passphrase;
        }

        var connection = new SqliteConnection(csb.ToString());
        await connection.OpenAsync(cancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    /// <summary>
    ///     The outbox's reportable state (ADR-0076): an open row makes the server refuse every MCP
    ///     tool call (ADR-0087, ToolGate), and doctor must say so. Guarded like the engine reads —
    ///     and the exit code only ever reflects a positively-read open row: absent table, no row,
    ///     `finished_at` set or a failed read all degrade (P1 §3 Decisions C/D). The catch is
    ///     broader than <see cref="SqliteException" /> on purpose: a malformed row (TEXT in an
    ///     INTEGER column) fails the Dapper mapping with InvalidCastException, and a diagnostic
    ///     must never crash on the bank it is diagnosing.
    /// </summary>
    private static async Task<MigrationState> ReadModelMigrationStateAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await TableExistsAsync(connection, "model_migration", cancellationToken))
            {
                return new MigrationState(MigrationRead.Unreadable, null);
            }

            var row = await connection.QuerySingleOrDefaultAsync<ModelMigrationRow>(new CommandDefinition(
                MemorySql.SelectModelMigration, cancellationToken: cancellationToken));
            return row is null || row.FinishedAt is not null
                ? new MigrationState(MigrationRead.None, null)
                : new MigrationState(MigrationRead.Open, row.StartedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new MigrationState(MigrationRead.Unreadable, null);
        }
    }

    private static string MigrationLine(MigrationState migration) => migration.Result switch
    {
        MigrationRead.Open =>
            $"model migration: open since {FormatTimestamp(migration.StartedAtUnix!.Value)} (all MCP tool calls are refused until it finishes)",
        MigrationRead.Unreadable => "model migration: unreadable",
        _ => "model migration: none open"
    };

    /// <summary>SQLITE_NOTADB (26): the file exists but is not a database (or not this key's database).</summary>
    private const int NotADatabaseErrorCode = 26;

    /// <summary>DateTimeOffset.FromUnixTimeSeconds' accepted bounds — checked before the call so a
    /// stored value outside them renders as data, not as a raw .NET parameter message.</summary>
    private const long MinUnixTimeSeconds = -62_135_596_800;

    private const long MaxUnixTimeSeconds = 253_402_300_799;

    /// <summary>Unix seconds as an absolute UTC instant — mirrors WatchCommands.FormatTimestamp (:185); the second copy is deliberate (R1 S8). An out-of-range value degrades to a marker naming the stored value (F7).</summary>
    private static string FormatTimestamp(long unixSeconds)
    {
        if (unixSeconds is < MinUnixTimeSeconds or > MaxUnixTimeSeconds)
        {
            return $"<unrepresentable unix time {unixSeconds}>";
        }

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private enum MigrationRead
    {
        None,
        Open,
        Unreadable
    }

    private sealed record MigrationState(MigrationRead Result, long? StartedAtUnix);

    /// <summary>model_migration's INTEGER unix-seconds columns as they are stored — Dapper has no DateTimeOffset handler in this solution, so mapping onto Core.Memory.ModelMigration would throw InvalidCastException (P2 §3.4).</summary>
    private sealed record ModelMigrationRow
    {
        public long StartedAt { get; init; }

        public long? FinishedAt { get; init; }
    }

    /// <summary>#522: what `embedding.threads` currently resolves to, and why (an explicit setting vs the halved-core default).</summary>
    private sealed record EmbeddingThreadsState(int Threads, string Source);

    private static partial class Log
    {
        [LoggerMessage(EventId = 1014, Level = LogLevel.Warning, Message = "doctor: could not resolve the encryption key")]
        public static partial void FailedToResolveEncryptionKey(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1015, Level = LogLevel.Warning, Message = "doctor: could not open the bank at {BankPath} read-only")]
        public static partial void FailedToOpenBank(ILogger logger, string bankPath, Exception exception);

        [LoggerMessage(EventId = 1016, Level = LogLevel.Warning, Message = "doctor: the bank at {BankPath} is not a SQLite database")]
        public static partial void BankIsNotADatabase(ILogger logger, string bankPath, Exception exception);
    }
}
