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
///     touches only two settings rows and one UPDATE, none of SqliteMemoryStore's write paths, so
///     it does not earn a third constructor parameter there.
/// </summary>
public sealed class SqliteCodeEngineStore(ISqliteConnectionFactory factory, IEmbeddingService embeddings,
    IEmbeddingManifestLoader manifestLoader, IManifestPoolingRepair poolingRepair) : ICodeEngineStore
{
    public async Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);

        // #470: correct a manifest the graph contradicts BEFORE anything reads it — this activation
        // invalidates every code vector regardless, so the fingerprint the rewrite moves costs
        // nothing here, and the next reconcile poll sees a stable engine instead of a second change.
        poolingRepair.Repair(fullPath);

        // B1: the CLI's own pre-flight (SettingsCommands.ModelSetCodeLocalAsync) is only a fast
        // local check — the HTTP settings endpoint (SettingsEndpoint.MapSettings) calls THIS
        // method directly with no CLI in the path, so this is the only refusal that actually
        // protects vec_code's fixed 768-dimension index and the code chunker's budget. Nothing is
        // written when either check refuses.
        var descriptor = manifestLoader.Load(fullPath);
        if (descriptor.Dimensions != CodeCorpusSchema.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Manifest '{fullPath}' declares {descriptor.Dimensions}-dimension embeddings, but the code " +
                $"corpus's vec_code index is fixed at {CodeCorpusSchema.EmbeddingDimensions} dimensions — there " +
                "is no dimension-reconcile phase for code, unlike the memory bank. Point 'model set code local' " +
                $"at a manifest with dimensions: {CodeCorpusSchema.EmbeddingDimensions}.");
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
            throw new InvalidOperationException(
                $"Manifest '{fullPath}' resolves to a {chunkBudget}-token chunk budget (min(510, context - " +
                $"reservation)), narrower than the {CodeChunker.DefaultBudget}-token chunks the code corpus's " +
                "chunker emits — that engine would silently truncate every chunk at embed time. Point " +
                $"'model set code local' at a manifest whose window is at least {CodeChunker.DefaultBudget} " +
                "content tokens.");
        }

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

            // The vec_code_pending trigger empties vec_code for every row this touches, so the old
            // vectors leave the searchable index the instant this commits — no stale-vector window,
            // and no reconcile phase needed since vec_code is a fixed float[768] (§3.3 D-E9).
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
