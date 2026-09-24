namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Test-only dispatcher for bank-open attempts: <see cref="SqliteConnectionFactory" /> always
///     calls through it, but nothing happens unless <see cref="TraceFileEnvVar" /> names a file — no
///     real launch ever sets that variable, so this stays a dispatcher, never a logic holder.
/// </summary>
public static class BankOpenObservation
{
    /// <summary>When set, every bank-open attempt in this process appends the bank path here.</summary>
    public const string TraceFileEnvVar = "AIRACCOON_TEST_BANK_OPEN_TRACE";

    private static readonly string? TraceFile = Environment.GetEnvironmentVariable(TraceFileEnvVar);

    /// <summary>A silent no-op unless a test named a trace file; a write failure never blocks the open it observes.</summary>
    public static void RecordOpenAttempt(string bankPath)
    {
        if (TraceFile is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(TraceFile, bankPath + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}
