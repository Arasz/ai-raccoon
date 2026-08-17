using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Drains rows a write, a watch digest, or a repair (e.g. `repair reingest`) left
///     `embed_state = 'pending'` (.NET-F1): fills the embedding column on already-committed rows
///     only — it never deletes, re-chunks or moves anything, unlike the explicit-only `repair`
///     verbs. On-demand, like <see cref="ModelMigrationJob" />: <see cref="HasWorkAsync" /> is the
///     only gate, so a bank with no configured embedding provider is legitimately never due — a
///     pending row with no engine has nothing to embed with, forever, not just until the next poll.
/// </summary>
public sealed class PendingEmbedJob(IEntryEmbedder embedder) : IMaintenanceJob
{
    public const string JobName = "pending-embed";

    public string Name => JobName;

    public string DisplayName => "embed rows left pending by a write, a watch digest or a repair";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    /// <summary>
    ///     4 generator batches per run (<see cref="EntryEmbedder.BatchSize" /> = 32): ADR-0076
    ///     measured ~72.6 rows/s draining the same generator path, so 128 rows costs ~1.8s —
    ///     comfortably inside the 15s on-demand poll
    ///     (<see cref="BankMaintenanceHostedService.OnDemandPollInterval" />). A full unbounded drain
    ///     would instead occupy that poll loop for minutes on a large backlog, starving every other
    ///     on-demand job behind it; <see cref="HasWorkAsync" /> brings this job back next poll for
    ///     whatever a run doesn't finish.
    /// </summary>
    private const int RowsPerRun = 4 * EntryEmbedder.BatchSize;

    public async Task<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var settings = await embedder.ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.Provider))
        {
            return false; // no engine configured: a pending row here is legitimately unembeddable
        }

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                MemorySql.HasPendingEmbed, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Embeds up to <see cref="RowsPerRun" /> pending rows; never leaves anything new pending itself.</summary>
    public async Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await embedder.EmbedPendingBatchAsync(connection, RowsPerRun, cancellationToken).ConfigureAwait(false);
        return false;
    }
}
