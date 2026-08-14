using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Write-path hook for shadow/dry-run mode (ADR-0039). Takes a <see cref="SqliteConnection" />,
///     so this stays Infrastructure-only rather than living in Core alongside the rest of the noise
///     abstractions. <see cref="NoOpNoiseShadowObserver" /> is <see cref="SqliteMemoryStore" />'s
///     default — see that class for why (TestData.cs, out of this lane's ownership, constructs
///     SqliteMemoryStore without this dependency).
/// </summary>
public interface INoiseShadowObserver
{
    Task<NoiseFilterResult> ObserveStoredWriteAsync(
        SqliteConnection connection, string projectId, string? agentId, long entryId, string hash,
        CancellationToken cancellationToken = default);

    Task ObserveRejectedWriteAsync(
        SqliteConnection connection, string projectId, string? agentId, string content, string policyName,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Shadow/dry-run mode (ADR-0039): runs the plumbing every future detector needs — reuse the
///     vector <c>EmbedIfConfiguredAsync</c> already computed (no second inference), fetch whatever
///     samples <see cref="INoiseClusterStore" /> holds for this scope, hand both to
///     <see cref="INoiseDetector" />, and record (never enforce) what it says. Gated by
///     <see cref="NoiseConfigKeys.LearnerShadowEnabledGlobal" />, default off.
///     <para>
///     <see cref="INoiseDetector" /> defaults to <see cref="NoOpNoiseDetector" /> — no scoring model
///     is shipped yet (two research lanes disagree on what generalizes; ADR-0039). This class's own
///     job is only to prove the seam is wired and exercised, not to decide anything.
///     </para>
///     <para>
///     Two entry points, called from SqliteMemoryStore.WriteAsync:
///     <see cref="ObserveStoredWriteAsync" /> for a normal write; <see cref="ObserveRejectedWriteAsync" />
///     feeds a confirmed rejection (e.g. a HermesProcessNoisePolicy hit) to
///     <see cref="NoiseFeedbackCollector" /> as a labelled negative. Shadow observations themselves
///     are never fed back as training data — only externally-confirmed rejections are — so nothing
///     here can reinforce its own guesses.
///     </para>
/// </summary>
public sealed partial class NoiseShadowObserver(
    INoiseClusterStore clusterStore,
    INoiseDetector detector,
    NoiseFeedbackCollector feedbackCollector,
    ILogger<NoiseShadowObserver> logger) : INoiseShadowObserver
{
    public async Task<NoiseFilterResult> ObserveStoredWriteAsync(
        SqliteConnection connection, string projectId, string? agentId, long entryId, string hash,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        if (!await IsShadowEnabledAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return NoiseFilterResult.Clean;
        }

        var embeddingBlob = await connection.ExecuteScalarAsync<byte[]?>(
                new CommandDefinition(NoiseClusterSql.SelectEntryEmbeddingById, new { Id = entryId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (embeddingBlob is null)
        {
            return NoiseFilterResult.Clean; // no embedding engine configured — degrades, same convention as the rest of the codebase
        }

        var vector = EmbeddingBlob.ToFloats(embeddingBlob);
        var storedSamples = await clusterStore.GetClustersAsync(projectId, agentId, cancellationToken).ConfigureAwait(false);

        var result = detector.Evaluate(vector, storedSamples);
        if (result.IsNoise)
        {
            Log.ShadowWouldReject(logger, projectId, hash, result.PolicyName ?? "unknown");
        }

        return result;
    }

    public async Task ObserveRejectedWriteAsync(
        SqliteConnection connection, string projectId, string? agentId, string content, string policyName,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        if (!await IsShadowEnabledAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await feedbackCollector.ProcessFeedbackAsync(projectId, agentId, content, policyName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> IsShadowEnabledAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        NoiseConfigKeys.ParseLearnerShadowEnabled(
            await ReadSettingAsync(connection, NoiseConfigKeys.LearnerShadowEnabledGlobal, cancellationToken).ConfigureAwait(false));

    private static Task<string?> ReadSettingAsync(SqliteConnection connection, string key, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<string?>(
            new CommandDefinition("SELECT value FROM settings WHERE key = @key", new { key }, cancellationToken: cancellationToken));

    private static partial class Log
    {
        [LoggerMessage(EventId = 951, Level = LogLevel.Information,
            Message = "Shadow mode: write in project {ProjectId} (hash {Hash}) flagged by detector {PolicyName} — not rejected, shadow mode never blocks (ADR-0039)")]
        public static partial void ShadowWouldReject(ILogger logger, string projectId, string hash, string policyName);
    }
}

/// <summary>
///     Null Object default for <see cref="SqliteMemoryStore" />'s legacy (pre-shadow-mode)
///     constructor — never touches the connection, never enabled. Not a nullable injected
///     parameter: a genuinely functioning, always-non-null implementation that does nothing.
/// </summary>
internal sealed class NoOpNoiseShadowObserver : INoiseShadowObserver
{
    public static readonly INoiseShadowObserver Instance = new NoOpNoiseShadowObserver();

    private NoOpNoiseShadowObserver()
    {
    }

    public Task<NoiseFilterResult> ObserveStoredWriteAsync(SqliteConnection connection, string projectId, string? agentId,
        long entryId, string hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(NoiseFilterResult.Clean);

    public Task ObserveRejectedWriteAsync(SqliteConnection connection, string projectId, string? agentId, string content,
        string policyName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
