using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite.Code;

/// <summary>
///     ICodeEngineStore's own small store (§3.3 D-E9), separate from SqliteMemoryStore: activation
///     touches two settings rows, the vec_code DDL and one UPDATE, none of SqliteMemoryStore's write
///     paths, so it does not earn a third constructor parameter there.
/// </summary>
public sealed class SqliteCodeEngineStore(ISqliteConnectionFactory factory, IEmbeddingService embeddings,
    IEmbeddingManifestLoader manifestLoader, IManifestPoolingRepair poolingRepair,
    IVecDimensionReconciler vecDimensions) : ICodeEngineStore
{
    public async Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);

        // B1: the CLI's own pre-flight (SettingsCommands.ModelSetCodeLocalAsync) is only a fast
        // local check — the HTTP settings endpoint (SettingsEndpoint.MapSettings) calls THIS
        // method directly with no CLI in the path. Nothing is written when a refusal throws.
        // #472: every refusal leg below throws CodeEngineActivationRefusedException (an
        // InvalidOperationException subtype) so the endpoint can catch just this refusal and
        // answer 4xx with the reason, instead of it escaping as a bare 500.
        EngineDescriptor descriptor;
        try
        {
            descriptor = manifestLoader.Load(fullPath);
        }
        catch (InvalidOperationException ex)
        {
            throw new CodeEngineActivationRefusedException(ex.Message, ex);
        }

        // #422: the chunker's budget is static (CodeChunker.DefaultBudget), so the only thing that
        // can actually go wrong is an engine whose window is NARROWER than the chunks it will be
        // handed — every one of those would be silently truncated at embed time, and the stored
        // vector would cover a prefix of the row's text with nothing recording that. A WIDER window
        // is accepted: under-filled chunks are a retrieval-quality trade, not a correctness fault.
        // The equality this used to require is what made the flagship model unactivatable, because
        // the constant it compared against was derived from a claim the graph contradicts.
        var chunkBudget = embeddings.ResolveChunkBudgetFor(new EmbeddingSettings("local", fullPath, null, null));
        if (chunkBudget < CodeChunker.DefaultBudget)
        {
            throw new CodeEngineActivationRefusedException(
                $"Manifest '{fullPath}' resolves to a {chunkBudget}-token chunk budget (min(510, context - " +
                $"reservation)), narrower than the {CodeChunker.DefaultBudget}-token chunks the code corpus's " +
                "chunker emits — that engine would silently truncate every chunk at embed time. Point " +
                $"'model code set local' at a manifest whose window is at least {CodeChunker.DefaultBudget} " +
                "content tokens.");
        }

        // #470: correct a manifest the graph contradicts — AFTER every refusal leg above, so a
        // refused activation never rewrites the user's file, and immediately BEFORE the fingerprint
        // is taken, so this activation's own unconditional invalidation is the only re-embed and
        // the code-reindex job's next poll sees a stable engine rather than a second change.
        poolingRepair.Repair(fullPath);

        var fingerprint = embeddings.EngineFingerprint("local", fullPath, null);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeModel, value = fullPath }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeEngine, value = fingerprint }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeDimensions, value = descriptor.Dimensions.ToString(CultureInfo.InvariantCulture) },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await vecDimensions.ReconcileAsync(connection, transaction, descriptor.Dimensions,
                VecDimensionReconciler.CodeVecTables, cancellationToken).ConfigureAwait(false);

            // The vec_code_pending trigger empties vec_code for every row this touches, so the old
            // vectors leave the searchable index the instant this commits — no stale-vector window.
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.MarkAllCodeEmbeddedPending,
                transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new EmbeddingConfig("local", fullPath, fingerprint);
    }
}
