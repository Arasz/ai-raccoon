using System.Globalization;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `doctor` (GH #357): verifies the bank's schema shape and reports — it never repairs. Reads
///     the bank read-only, exactly like <c>OpenSnapshotReadOnly</c> (AppRegistrations.cs), so it
///     never runs <c>MemorySchema.EnsureAsync</c> against the very bank it is inspecting (ADR-0075
///     sanctions the CLI reading the bank directly; never writing).
/// </summary>
public sealed partial class DoctorCommands(ISqliteConnectionFactory bankConnectionFactory, IEncryptionKeyResolver keyResolver, ILogger<DoctorCommands> logger)
{
    public async Task<int> RunAsync(StandardStreams streams, CancellationToken cancellationToken)
    {
        var bankPath = bankConnectionFactory.BankPath;
        if (!File.Exists(bankPath))
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: doctor: no bank at {bankPath}");
            return ExitCode.NoBank;
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
            return ExitCode.FailedToResolveEncryptionKey;
        }

        SqliteConnection connection;
        try
        {
            connection = await OpenBankReadOnlyAsync(bankPath, resolvedKey.Passphrase, cancellationToken);
        }
        catch (SqliteException ex)
        {
            Log.FailedToOpenBank(logger, bankPath, ex);
            await streams.WriteErrorLineAsync($"ai-raccoon: doctor: could not open the bank read-only: {ex.Message}");
            return ExitCode.FailedToOpenEncryptedBank;
        }

        await using (connection)
        {
            var report = await SchemaDoctor.DiagnoseAsync(connection, cancellationToken);
            var code = await ReadCodeEngineStateAsync(connection, cancellationToken);
            var threads = await ReadEmbeddingThreadsStateAsync(connection, cancellationToken);
            return await ReportAsync(bankPath, report, code, threads, streams);
        }
    }

    private static async Task<int> ReportAsync(string bankPath, SchemaDoctorReport report,
        CodeEngineState code, EmbeddingThreadsState threads, StandardStreams streams)
    {
        await streams.WriteOutputLineAsync($"ai-raccoon doctor: {bankPath}");
        await streams.WriteOutputLineAsync($"user_version: {report.StoredVersion} (this binary: {report.CurrentVersion})");
        await streams.WriteOutputLineAsync($"application_id: {report.StoredDigest} (expected: {report.ExpectedDigest})");
        await streams.WriteOutputLineAsync(code.Directory is null
            ? $"code engine: not configured — run '{CodeEngineSetup.DefaultModelCommand}' to enable semantic code search"
            : $"code engine: {code.Model} ({code.Directory})");
        // #522: what `embedding.threads` currently resolves to — the same resolver a real local
        // session build uses (EmbeddingService), so this never drifts from EmbeddingSessionLog's
        // live confirmation.
        await streams.WriteOutputLineAsync($"embedding threads: {threads.Threads} ({threads.Source})");
        await streams.WriteOutputLineAsync(
            $"code rows pending: {code.PendingRows?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}");
        await streams.WriteOutputLineAsync("doctor verifies schema shape only; it never repairs a bank");

        switch (report.Status)
        {
            case SchemaDoctorStatus.VersionAheadOfBinary:
                await streams.WriteOutputLineAsync(
                    $"status: SCHEMA NEWER THAN THIS BINARY (bank is v{report.StoredVersion}, this binary supports up to v{report.CurrentVersion}) — update ai-raccoon");
                return ExitCode.SchemaNewerThanBinary;

            case SchemaDoctorStatus.Healthy:
                await streams.WriteOutputLineAsync("status: HEALTHY");
                return ExitCode.Success;

            default:
                await streams.WriteOutputLineAsync($"status: SHAPE MISMATCH ({report.Findings.Count} finding(s))");
                foreach (var finding in report.Findings)
                {
                    await streams.WriteOutputLineAsync($"  - {finding.ObjectName}: {finding.Detail}");
                }

                return ExitCode.SchemaVerificationFailed;
        }
    }

    /// <summary>
    ///     #422: the code corpus is the one subsystem that can be completely inert without anything
    ///     saying so — rows ingest and sit `pending` forever with no engine, which is legitimate
    ///     rather than an error, so no log line and no failure ever mentions it. doctor is where
    ///     someone looks when search feels wrong, so the state and its remedy are reported here.
    ///     The count is a plain COUNT, tolerant of a bank predating `code_entries`.
    /// </summary>
    private static async Task<CodeEngineState> ReadCodeEngineStateAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // A shape-mismatched bank is exactly the bank doctor exists to diagnose, so this extra
        // read must never be what decides the exit code: every table it touches may be missing or
        // the wrong shape. Guarded by existence and by catch, and the report says so.
        try
        {
            var directory = await TableExistsAsync(connection, "settings", cancellationToken)
                ? await ReadSettingAsync(connection, EmbeddingSettingsKeys.CodeModel, cancellationToken)
                : null;
            var pending = await CountPendingCodeRowsAsync(connection, cancellationToken);
            return string.IsNullOrWhiteSpace(directory)
                ? new CodeEngineState(null, null, pending)
                : new CodeEngineState(ModelNameFor(directory), directory, pending);
        }
        catch (SqliteException)
        {
            return new CodeEngineState(null, null, null);
        }
    }

    /// <summary>
    ///     #522: `embedding.threads` resolves the same way for every local session (memory or
    ///     code), so doctor reports what it currently resolves to — using
    ///     <see cref="EmbeddingService" />'s own resolver, never a second copy of the halving
    ///     default. Guarded exactly like <see cref="ReadCodeEngineStateAsync" />: a shape-mismatched
    ///     bank must never turn this extra read into doctor's own exit code.
    /// </summary>
    private static async Task<EmbeddingThreadsState> ReadEmbeddingThreadsStateAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var raw = await TableExistsAsync(connection, "settings", cancellationToken)
                ? await ReadSettingAsync(connection, EmbeddingSettingsKeys.Threads, cancellationToken)
                : null;
            var threads = EmbeddingService.TryParseThreadsSetting(raw, out var explicitThreads)
                ? explicitThreads
                : EmbeddingService.HalvedCoreThreadDefault(Environment.ProcessorCount);
            return new EmbeddingThreadsState(threads, EmbeddingService.ThreadCountSource(raw));
        }
        catch (SqliteException)
        {
            return new EmbeddingThreadsState(EmbeddingService.HalvedCoreThreadDefault(Environment.ProcessorCount),
                EmbeddingService.ThreadCountSource(null));
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: cancellationToken)) > 0;

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

    private static async Task<long?> CountPendingCodeRowsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "code_entries", cancellationToken))
        {
            return 0;
        }

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM code_entries WHERE embed_state = 'pending'",
            cancellationToken: cancellationToken));
    }

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key", new { key }, cancellationToken: cancellationToken));

    /// <summary>Null <paramref name="Directory" /> is "no code engine configured".</summary>
    private sealed record CodeEngineState(string? Model, string? Directory, long? PendingRows);

    /// <summary>#522: what `embedding.threads` currently resolves to, and why (an explicit setting vs the halved-core default).</summary>
    private sealed record EmbeddingThreadsState(int Threads, string Source);

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
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "doctor: could not resolve the encryption key")]
        public static partial void FailedToResolveEncryptionKey(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "doctor: could not open the bank at {BankPath} read-only")]
        public static partial void FailedToOpenBank(ILogger logger, string bankPath, Exception exception);
    }
}
