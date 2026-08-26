using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
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
            var code = await ReadCorpusEngineStateAsync(connection, CorpusEngineProbe.Code, cancellationToken);
            var threads = await ReadEmbeddingThreadsStateAsync(connection, cancellationToken);
            return await ReportAsync(bankPath, report, code, threads, streams);
        }
    }

    private static async Task<int> ReportAsync(string bankPath, SchemaDoctorReport report,
        CorpusEngineState? code, EmbeddingThreadsState threads, StandardStreams streams)
    {
        await streams.WriteOutputLineAsync($"ai-raccoon doctor: {bankPath}");
        await streams.WriteOutputLineAsync($"user_version: {report.StoredVersion} (this binary: {report.CurrentVersion})");
        await streams.WriteOutputLineAsync($"application_id: {report.StoredDigest} (expected: {report.ExpectedDigest})");
        await streams.WriteOutputLineAsync(CorpusEngineLines.EngineLine(CorpusEngineProbe.Code, code));
        // #522: what `embedding.threads` resolves to, via EmbeddingService's own resolver.
        await streams.WriteOutputLineAsync(
            $"embedding threads: {EmbeddingService.ThreadCountDisplay(threads.Threads)} ({threads.Source})");
        await streams.WriteOutputLineAsync(CorpusEngineLines.PendingLine(CorpusEngineProbe.Code, code));
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

                await streams.WriteOutputLineAsync(
                    "remedy: start the server (ai-raccoon serve) — it repairs the schema on every open");
                return ExitCode.SchemaVerificationFailed;
        }
    }

    /// <summary>
    ///     One corpus's engine state (#422 for code, this task for memory). A shape-mismatched bank is
    ///     exactly the bank doctor exists to diagnose, so this extra read must never be what decides the
    ///     exit code: every table it touches may be missing or the wrong shape. Guarded by existence and
    ///     by catch, and the report says so. A null return is "unreadable"; Value null is "not configured".
    /// </summary>
    private static async Task<CorpusEngineState?> ReadCorpusEngineStateAsync(SqliteConnection connection,
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
            return new CorpusEngineState(null, null, null, null);
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
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    /// <summary>#522: what `embedding.threads` currently resolves to, and why (an explicit setting vs the halved-core default).</summary>
    private sealed record EmbeddingThreadsState(int Threads, string Source);

    private static partial class Log
    {
        [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "doctor: could not resolve the encryption key")]
        public static partial void FailedToResolveEncryptionKey(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "doctor: could not open the bank at {BankPath} read-only")]
        public static partial void FailedToOpenBank(ILogger logger, string bankPath, Exception exception);
    }
}
