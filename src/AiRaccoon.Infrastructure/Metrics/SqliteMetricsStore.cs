using System.Text.Json;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     Batch-writes measurements into the bank's `metrics` table (WP0 schema). Enforces the
///     save-time query-identity allowlist: a measurement's query identity is exactly
///     {query_hash, correlation_id} — a row whose Tags carry query text is rejected on save, and
///     the check fails closed on anything it cannot verify as safe
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
    ///     Tag keys that would carry raw query text rather than the allowed hash/correlation-id
    ///     identity. Matched case-insensitively, so this is a positive allowlist enforced by
    ///     exclusion: everything else in Tags is free-form metadata, not query identity.
    /// </summary>
    private static readonly HashSet<string> ForbiddenTagKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "query", "queryText", "search_query", "q", "text"
    };

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

    private static object ToParameters(Measurement measurement) => new
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

    /// <summary>Fails closed: Tags that are not a verifiably safe JSON object are treated as carrying query text.</summary>
    private static bool CarriesQueryText(Measurement measurement)
    {
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

            return document.RootElement.EnumerateObject().Any(property => ForbiddenTagKeys.Contains(property.Name));
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 961, Level = LogLevel.Warning,
            Message = "Rejected {Count} measurement(s) on save: Tags carried query text outside the query_hash/correlation_id allowlist")]
        public static partial void RowsRejected(ILogger logger, int count);
    }
}
