using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Setup;

/// <summary>
///     Quiet mode's logging destination (owner ruling 2026-08-09): every level goes to one file
///     beside the bank, nothing reaches stdout/stderr. No rotation (D5, owner-approved) — the
///     file accumulates across the installation's lifetime, since every Hermes-spawned
///     connection appends to the same path.
/// </summary>
internal static class QuietLogging
{
    private const string FileName = "quiet.log";

    internal static void Configure(ILoggingBuilder loggingBuilder, InfrastructureOptions options)
    {
        loggingBuilder.ClearProviders();
        loggingBuilder.SetMinimumLevel(LogLevel.Trace);
        loggingBuilder.AddProvider(new QuietFileLoggerProvider(LogFilePath(options)));
    }

    /// <summary>The log file's path: beside the bank this installation already resolves, so a
    /// quiet-mode process needs no second writable-directory convention.</summary>
    internal static string LogFilePath(InfrastructureOptions options) =>
        Path.Combine(Path.GetDirectoryName(SqliteConnectionFactory.BankPathFor(options))!, FileName);
}
