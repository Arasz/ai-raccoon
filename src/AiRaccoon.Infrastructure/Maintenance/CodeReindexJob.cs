using AiRaccoon.Core.EventPump;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Signals the embed topic's single consumer (<see cref="EmbedDrainService" />, WP11-B2) when
///     `code_entries` rows are left `embed_state = 'pending'` by a code-engine activation or a
///     fingerprint change (§3.3 D-E9, §3.8) — it no longer drains inline itself. No outbox, no
///     <see cref="IModelMigrationLease" /> — activation invalidates pending in its OWN transaction
///     (<see cref="ICodeEngineStore" />) and returns; this job's own NEXT poll signals the drain, so
///     no vector write ever lands inside the configure transaction. No ToolGate interaction: memory
///     tools are never blocked while code rows drain. On-demand, like <see cref="PendingEmbedJob" />:
///     <see cref="HasWorkAsync" /> reads <see cref="ICodeEmbedder.HasPendingWorkAsync" />, which is
///     false whenever no code engine is configured — a pending code row with no engine is
///     legitimately unembeddable, forever, so the job stays quiet (no error spam) rather than
///     polling a permanently-pending backlog.
/// </summary>
public sealed class CodeReindexJob(ICodeEmbedder embedder, IEventPump<EmbedDrainRequest> embedDrainPump)
    : IMaintenanceJob, IReportsOutstandingRows
{
    public const string JobName = "code-reindex";

    public string Name => JobName;

    public string DisplayName => "re-embed code corpus rows left pending by an engine activation or fingerprint change";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    /// <summary>
    ///     S6: reconciles the stored fingerprint against a freshly-computed one BEFORE reporting
    ///     due-ness — a manifest that changed in place since activation must invalidate on its own,
    ///     not only when a re-activation happens to run. Runs on every poll (15s on-demand cadence),
    ///     same as every other on-every-open reconcile in this codebase.
    /// </summary>
    public async ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await embedder.ReconcileFingerprintAsync(connection, cancellationToken).ConfigureAwait(false);
        return await embedder.HasPendingWorkAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signals the embed topic instead of embedding inline (WP11-B2); never leaves anything new pending itself.</summary>
    public ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code));
        return ValueTask.FromResult(false);
    }

    /// <summary>WP3 (#477): `job.code-reindex.rows` — the bank-wide backlog immediately after signalling the drain, before it has run.</summary>
    public async ValueTask<long> CountOutstandingRowsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            MemorySql.CountPendingCodeEmbed, cancellationToken: cancellationToken)).ConfigureAwait(false);
}
