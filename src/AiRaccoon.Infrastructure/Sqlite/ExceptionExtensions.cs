using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Extensions over <see cref="Exception" /> that only make sense for the SQLite-backed bank.
/// </summary>
public static class ExceptionExtensions
{
    /// <summary>
    ///     SQLITE_BUSY (5) / SQLITE_LOCKED (6) anywhere in the exception chain: another writer holds
    ///     the bank's write lock past the busy timeout (WP12's write-lock convoy). Transient
    ///     contention, so the callers that share this classification defer or refuse on it, while
    ///     genuine SQLite faults (26, "file is not a database") stay on their own path. The chain
    ///     walk is the point: a layer may wrap the original busy error. One home so the extraction
    ///     pass, the tool filter, the best-effort search-quality write, the access-rating bump, the
    ///     VACUUM swallow and the metrics-flush retry cannot drift apart.
    /// </summary>
    /// <returns>true when the chain carries code 5 or 6; false otherwise (including a null receiver).</returns>
    public static bool IsBankBusy(this Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }
        }

        return false;
    }
}
