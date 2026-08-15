namespace AiRaccoon.Core.Metrics;

/// <summary>
///     The universal measurement row (docs/plans/2026-08-15-performance-metrics-implementation.md).
///     Query identity is exactly <see cref="QueryHash" /> + <see cref="CorrelationId" /> — a
///     measurement carrying query text is rejected at save time (WP3's allowlist), not here.
/// </summary>
public sealed record Measurement(
    string Name,
    MeasurementKind Kind,
    double Value,
    string Unit,
    DateTimeOffset RecordedAt,
    string? ProjectId = null,
    string? QueryHash = null,
    string? CorrelationId = null,
    string? Tags = null);
