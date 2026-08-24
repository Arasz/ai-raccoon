using System.Globalization;
using AiRaccoon.Core.Memory;
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
public sealed class SqliteCodeEngineStore(
    ISqliteConnectionFactory factory,
    IEmbeddingService embeddings,
    IEmbeddingManifestLoader manifestLoader,
    IManifestPoolingRepair poolingRepair,
    IVecDimensionReconciler vecDimensions) : ICodeEngineStore
{
    public async Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);

        EngineDescriptor descriptor;
        try
        {
            descriptor = manifestLoader.Load(fullPath);
        }
        catch (InvalidOperationException ex)
        {
            throw new CodeEngineActivationRefusedException(ex.Message, ex);
        }

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

            await vecDimensions.ReconcileCodeAsync(connection, transaction, descriptor.Dimensions, cancellationToken).ConfigureAwait(false);


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
