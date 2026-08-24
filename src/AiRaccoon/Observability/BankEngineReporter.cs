using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;

namespace AiRaccoon.Observability;

/// <summary>
///     States, at startup, which binary is running and which embedding engine the bank was written
///     with (WP3 step 5). Drift between the two was only visible through <c>ps</c> against the
///     binary's mtime.
/// </summary>
public sealed partial class BankEngineReporter(ISqliteConnectionFactory factory, ILogger<BankEngineReporter> logger)
{
    /// <summary>Best effort: a startup diagnostic must never be why a process fails to start.</summary>
    public async Task ReportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
            var engine = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT value FROM settings WHERE key = @key",
                    new { key = EmbeddingSettingsKeys.Engine }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            // "unset" rather than an absent line: a bank whose engine was never configured is the
            // case that produced the drift (ADR-0063), and silence would read as nothing to report.
            Log.BankEngine(logger, ServerInfo.BinaryVersion, engine ?? "unset");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.BankEngineUnreadable(logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 13, Level = LogLevel.Information,
            Message = "ai-raccoon: running {BinaryVersion} against a bank embedded with {BankEngine}")]
        public static partial void BankEngine(ILogger logger, string binaryVersion, string bankEngine);

        [LoggerMessage(EventId = 14, Level = LogLevel.Debug,
            Message = "ai-raccoon: could not read the bank's embedding engine for the startup line")]
        public static partial void BankEngineUnreadable(ILogger logger, Exception exception);
    }
}
