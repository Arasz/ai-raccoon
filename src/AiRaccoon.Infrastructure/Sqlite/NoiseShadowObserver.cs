using AiRaccoon.Core.Memory.Filtering;
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
        SqliteConnection connection, string projectId, string? agentId, string content, string hash,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Shadow/dry-run mode (ADR-0039, amended by this task): hands the stored write's own content
///     to <see cref="INoiseDetector" /> and records (never enforces) what it says. No vector, no
///     stored-sample lookup — the research settled on a structural/lexical classifier over the raw
///     content, not an embedding comparison, so there is nothing left to reuse or fetch here.
///     Gated by <see cref="NoiseConfigKeys.LearnerShadowEnabledGlobal" />, default off.
///     <para>
///     <see cref="INoiseDetector" /> defaults to <see cref="NoOpNoiseDetector" /> — no scoring model
///     is shipped yet (ADR-0039). This class's own job is only to prove the seam is wired and
///     exercised, not to decide anything.
///     </para>
/// </summary>
public sealed partial class NoiseShadowObserver(
    INoiseDetector detector,
    ILogger<NoiseShadowObserver> logger) : INoiseShadowObserver
{
    public async Task<NoiseFilterResult> ObserveStoredWriteAsync(
        SqliteConnection connection, string projectId, string? agentId, string content, string hash,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        if (!await IsShadowEnabledAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return NoiseFilterResult.Clean;
        }

        var result = detector.Evaluate(content);
        if (result.IsNoise)
        {
            Log.ShadowWouldReject(logger, projectId, hash, result.PolicyName ?? "unknown");
        }

        return result;
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
        string content, string hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(NoiseFilterResult.Clean);
}
