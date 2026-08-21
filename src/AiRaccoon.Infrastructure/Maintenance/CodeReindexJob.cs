using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Drains `code_entries` rows a code-engine activation or a fingerprint change left
///     `embed_state = 'pending'` (§3.3 D-E9, §3.8): no outbox, no <see cref="IModelMigrationLease" />
///     — activation invalidates pending in its OWN transaction (<see cref="ICodeEngineStore" />) and
///     returns; this job's own NEXT poll does the drain, so no vector write ever lands inside the
///     configure transaction. No ToolGate interaction: memory tools are never blocked while code
///     rows drain. On-demand, like <see cref="PendingEmbedJob" />: <see cref="HasWorkAsync" /> reads
///     <see cref="ICodeEmbedder.HasPendingWorkAsync" />, which is false whenever no code engine is
///     configured — a pending code row with no engine is legitimately unembeddable, forever, so the
///     job stays quiet (no error spam) rather than polling a permanently-pending backlog.
/// </summary>
public sealed class CodeReindexJob(ICodeEmbedder embedder) : IMaintenanceJob
{
    public const string JobName = "code-reindex";

    public string Name => JobName;

    public string DisplayName => "re-embed code corpus rows left pending by an engine activation or fingerprint change";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    /// <summary>One drain batch per run (§3.3 D-E9: "batches of 32") — <see cref="HasWorkAsync" /> brings this job back next poll for whatever a run doesn't finish.</summary>
    private const int RowsPerRun = 32;

    public async Task<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await embedder.HasPendingWorkAsync(connection, cancellationToken).ConfigureAwait(false);

    /// <summary>Embeds up to <see cref="RowsPerRun" /> pending code rows; never leaves anything new pending itself.</summary>
    public async Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await embedder.EmbedPendingBatchAsync(connection, RowsPerRun, cancellationToken).ConfigureAwait(false);
        return false;
    }
}
