using AiRaccoon.Core.EventPump;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Signals the embed topic's single consumer (<see cref="EmbedDrainService" />, WP11-B2) when
///     a write, a watch digest, or a repair (e.g. `repair reingest`) left
///     `embed_state = 'pending'` (.NET-F1) — it no longer drains inline itself. On-demand, like
///     <see cref="ModelMigrationJob" />: <see cref="HasWorkAsync" /> is the only gate, so a bank
///     with no configured embedding provider is legitimately never due — a pending row with no
///     engine has nothing to embed with, forever, not just until the next poll.
/// </summary>
public sealed class PendingEmbedJob(IEntryEmbedder embedder, IEventPump<EmbedDrainRequest> embedDrainPump) : IMaintenanceJob
{
    public const string JobName = "pending-embed";

    public string Name => JobName;

    public string DisplayName => "embed rows left pending by a write, a watch digest or a repair";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    public async ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken)
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

    /// <summary>
    ///     Signals the embed topic instead of embedding inline (WP11-B2): two overlapping passes —
    ///     the heavy pass and the 15s on-demand poll both call <see cref="MaintenanceJobRunner.RunDueAsync" />
    ///     — used to both select and embed the same rows; now the second signal coalesces away and
    ///     <see cref="EmbedDrainService" />'s single reader drains once.
    /// </summary>
    public ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory));
        return ValueTask.FromResult(false);
    }
}
