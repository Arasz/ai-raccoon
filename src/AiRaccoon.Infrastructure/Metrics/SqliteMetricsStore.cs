using System.Text.Json;
using System.Text.RegularExpressions;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     Batch-writes measurements into the bank's `metrics` table (WP0 schema). Enforces the
///     save-time query-identity allowlist: a measurement's query identity is exactly
///     {query_hash, correlation_id} — both must match their real shape, every Tags key must be on
///     a known-good list and its value must match that key's known-good shape too, and the check
///     fails closed on anything it cannot verify as safe
///     (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3 AC6).
/// </summary>
public sealed partial class SqliteMetricsStore(ISqliteConnectionFactory factory, ILogger<SqliteMetricsStore> logger)
    : IMetricsStore
{
    private const string InsertSql = """
                                     INSERT INTO metrics (name, kind, value, unit, project_id, query_hash, correlation_id, tags, recorded_at)
                                     VALUES (@Name, @Kind, @Value, @Unit, @ProjectId, @QueryHash, @CorrelationId, @Tags, @RecordedAt)
                                     """;

    /// <summary>
    ///     Tags keys allowed at save time — a positive allowlist, not a list of known-bad keys to
    ///     exclude. Derived from every current caller of the Measurement constructor: production
    ///     code (ToolExecutionActivity, MemoryTools) sets no Tags at all today; "phase" is the one
    ///     key any caller uses (MemoryTools' search-phase measurements, exercised by
    ///     SqliteMetricsStoreTests), and its value must be one of SearchTimings' own series suffixes —
    ///     checked below, not just the key.
    /// </summary>
    private static readonly HashSet<string> AllowedTagKeys = new(StringComparer.Ordinal) { "phase" };

    /// <summary>The series suffixes "phase" may legitimately hold — derived from SearchTimings, not a second hand-kept list.</summary>
    private static readonly HashSet<string> AllowedPhaseValues =
        SearchTimings.SeriesNames.Select(name => name["search.".Length..]).ToHashSet(StringComparer.Ordinal);

    /// <summary>QueryHash's real shape: SHA-256 lowercase hex, matching ContentHash.OfValue's output.</summary>
    private static readonly Regex QueryHashShape = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>CorrelationId's real shape: Guid.CreateVersion7().ToString("N") — 32 lowercase hex digits.</summary>
    private static readonly Regex CorrelationIdShape = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    public async Task SaveBatchAsync(IReadOnlyList<Measurement> measurements, CancellationToken cancellationToken = default)
    {
        if (measurements.Count == 0)
        {
            return;
        }

        var allowed = measurements.Where(m => !CarriesQueryText(m)).ToList();
        var rejected = measurements.Count - allowed.Count;
        if (rejected > 0)
        {
            Log.RowsRejected(logger, rejected);
        }

        if (allowed.Count == 0)
        {
            return;
        }

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var measurement in allowed)
        {
            await connection.ExecuteAsync(new CommandDefinition(InsertSql, ToParameters(measurement), transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object ToParameters(Measurement measurement) =>
        new
        {
            measurement.Name,
            Kind = measurement.Kind.ToString(),
            measurement.Value,
            measurement.Unit,
            measurement.ProjectId,
            measurement.QueryHash,
            measurement.CorrelationId,
            measurement.Tags,
            RecordedAt = measurement.RecordedAt.ToUnixTimeSeconds()
        };

    /// <summary>
    ///     Fails closed: QueryHash/CorrelationId that do not match their real shape, and Tags that
    ///     are not a verifiably safe JSON object with only allowlisted keys and values, are all
    ///     treated as carrying query text.
    /// </summary>
    private static bool CarriesQueryText(Measurement measurement)
    {
        if (measurement.QueryHash is not null && !QueryHashShape.IsMatch(measurement.QueryHash))
        {
            return true;
        }

        if (measurement.CorrelationId is not null && !CorrelationIdShape.IsMatch(measurement.CorrelationId))
        {
            return true;
        }

        if (string.IsNullOrEmpty(measurement.Tags))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(measurement.Tags);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            return document.RootElement.EnumerateObject().Any(property => !IsAllowedTag(property));
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool IsAllowedTag(JsonProperty property) =>
        AllowedTagKeys.Contains(property.Name)
        && property.Value.ValueKind == JsonValueKind.String
        && AllowedPhaseValues.Contains(property.Value.GetString() ?? string.Empty);

    private static partial class Log
    {
        [LoggerMessage(EventId = 961, Level = LogLevel.Warning,
            Message = "Rejected {Count} measurement(s) on save: failed the query-identity allowlist (Tags key/value, or QueryHash/CorrelationId shape)")]
        public static partial void RowsRejected(ILogger logger, int count);
    }
}
