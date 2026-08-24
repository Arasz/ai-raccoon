using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     The code corpus's own embedding mechanics (WP5, docs/work/2026-08-21-code-search-implementation-plan.md
///     §12.2 H5): resolves the code engine from `embedding.codeModel`/`embedding.codeEngine`
///     through <see cref="IEmbeddingService.CreateGenerator" /> — the same keyed cache the memory
///     engine uses, keyed by fingerprint, so a second engine coexists there without touching
///     <see cref="IEmbeddingService" /> internals. No <see cref="IModelMigrationLease" /> anywhere
///     in this path: the code corpus has no outbox (§3.3 D-E9).
/// </summary>
public interface ICodeEmbedder
{
    /// <summary>
    ///     Embeds a query with the configured code engine, trimmed to its manifest window (126
    ///     tokens for code-daemon-embed-v1) — <see cref="QueryVector.Trimmed" /> is set when the
    ///     trim actually cut the query. Returns <see cref="QueryVector.Empty" /> when no code
    ///     engine is configured (code search degrades to FTS5-only). Throws
    ///     <see cref="AiRaccoon.Core.Memory.Code.CodeEngineUnloadableException" /> when a code
    ///     engine IS configured but its manifest or model/tokenizer files fail to load.
    /// </summary>
    Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Embeds up to <paramref name="limit" /> bank-wide `code_entries` rows left
    ///     `embed_state = 'pending'`, with the configured engine. A no-op (returns 0) when no code
    ///     engine is configured — a pending code row with no engine is legitimately unembeddable,
    ///     same rule as <see cref="IEntryEmbedder.EmbedPendingBatchAsync" />. Throws
    ///     <see cref="AiRaccoon.Core.Memory.Code.CodeEngineUnloadableException" /> only when the
    ///     engine itself fails to load (broken manifest/model files) — a single row whose CONTENT
    ///     breaks generation (S2) does not throw: it falls back to per-row embedding, so the rest of
    ///     the batch still makes progress, and a row that keeps failing on its own is left pending
    ///     with a bumped attempt count rather than retried forever. Returns the count actually
    ///     marked embedded, which can be less than the rows selected when a concurrent activation
    ///     (S1) or a poison row (S2) left some of them pending.
    /// </summary>
    Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     True only when a code engine is configured AND at least one `code_entries` row is
    ///     pending — mirrors <c>PendingEmbedJob.HasWorkAsync</c>'s "no engine ⇒ never due" rule, so
    ///     the code-reindex maintenance job stays quiet (no error spam) while unconfigured.
    /// </summary>
    Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken);

    /// <summary>
    ///     S6: compares the configured code-model directory's freshly-computed fingerprint against
    ///     the stored `embedding.codeEngine` value; on a mismatch (the manifest on disk changed
    ///     since the last activation/reconcile — a re-download, a changed pin) invalidates every
    ///     embedded `code_entries` row to 'pending' and updates the stored fingerprint, in one
    ///     transaction — the SAME invalidation <see cref="AiRaccoon.Core.Memory.ICodeEngineStore.ActivateCodeEngineAsync" />
    ///     performs. A no-op (returns false) when no code engine is configured, or when the stored
    ///     fingerprint already matches. Meant to be called on every poll (CodeReindexJob.HasWorkAsync),
    ///     not just on an explicit re-activation.
    /// </summary>
    Task<bool> ReconcileFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken);

    /// <summary>
    ///     Brings `vec_code` to the dimension the configured code engine embeds at (D3 mirror for
    ///     the code corpus, vec-code-unfix-dim): reads `embedding.codeModel` (blank → no-op, false),
    ///     resolves the target from `embedding.codeDimensions` (missing → 768, the pre-1.35
    ///     default), and recreates the table when missing or mismatched. DDL-only — never touches
    ///     row state, so a reconciled-away table degrades to FTS-only until the next activation or
    ///     fingerprint change, exactly like the memory bank's open reconcile. Server-only by
    ///     construction: called from NodeRunner's open path, never from a CLI verb.
    /// </summary>
    Task<bool> ReconcileVecCodeDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken);
}
