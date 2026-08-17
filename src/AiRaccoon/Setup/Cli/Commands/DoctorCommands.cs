using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
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
            await streams.WriteOutputLineAsync("no bank to check");
            return ExitCode.Success;
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
            return await ReportAsync(bankPath, report, streams);
        }
    }

    private static async Task<int> ReportAsync(string bankPath, SchemaDoctorReport report, StandardStreams streams)
    {
        await streams.WriteOutputLineAsync($"ai-raccoon doctor: {bankPath}");
        await streams.WriteOutputLineAsync($"user_version: {report.StoredVersion} (this binary: {report.CurrentVersion})");
        await streams.WriteOutputLineAsync($"application_id: {report.StoredDigest} (expected: {report.ExpectedDigest})");
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
