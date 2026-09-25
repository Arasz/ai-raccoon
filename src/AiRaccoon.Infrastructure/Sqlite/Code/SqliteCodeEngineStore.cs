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
        // The bundled engine is stored by name: its directory moves with every tool version (ADR-0108).
        var bundled = BundledModel.IsBundled(directory);
        var fullPath = bundled ? BundledModel.SettingValue : Path.GetFullPath(directory);
        var manifestDirectory = bundled ? BundledModel.ResolveDirectory() : fullPath;

        EngineDescriptor descriptor;
        try
        {
            descriptor = manifestLoader.Load(manifestDirectory);
        }
        catch (InvalidOperationException ex)
        {
            throw new CodeEngineActivationRefusedException(ex.Message, ex);
        }

        // The code corpus chunks at its own budget, so what matters is the engine's window, not its memory chunk budget.
        var chunkBudget = Math.Min(EmbeddingService.MaxManifestChunkTokens, descriptor.ContextWindowTokens - descriptor.SpecialTokenReservation);
        if (chunkBudget < CodeChunker.DefaultBudget)
        {
            throw new CodeEngineActivationRefusedException(
                $"Manifest '{fullPath}' resolves to a {chunkBudget}-token chunk budget (min(510, context - " +
                $"reservation)), narrower than the {CodeChunker.DefaultBudget}-token chunks the code corpus's " +
                "chunker emits — that engine would silently truncate every chunk at embed time. Point " +
                $"'model code set local' at a manifest whose window is at least {CodeChunker.DefaultBudget} " +
                "content tokens.");
        }

        if (!bundled)
        {
            poolingRepair.Repair(fullPath);
        }

        var fingerprint = embeddings.EngineFingerprint("local", fullPath, null);

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeModel, value = fullPath }, transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeEngine, value = fingerprint }, transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeDimensions, value = descriptor.Dimensions.ToString(CultureInfo.InvariantCulture) },
                transaction, cancellationToken: cancellationToken));

            await vecDimensions.ReconcileCodeAsync(connection, transaction, descriptor.Dimensions, cancellationToken);


            await connection.ExecuteAsync(new CommandDefinition(MemorySql.MarkAllCodeEmbeddedPending,
                transaction: transaction, cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new EmbeddingConfig("local", fullPath, fingerprint);
    }
}
