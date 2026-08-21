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
    ///     <see cref="AiRaccoon.Core.Memory.Code.CodeEngineUnloadableException" /> when configured
    ///     but unloadable.
    /// </summary>
    Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     True only when a code engine is configured AND at least one `code_entries` row is
    ///     pending — mirrors <c>PendingEmbedJob.HasWorkAsync</c>'s "no engine ⇒ never due" rule, so
    ///     the code-reindex maintenance job stays quiet (no error spam) while unconfigured.
    /// </summary>
    Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken);
}
