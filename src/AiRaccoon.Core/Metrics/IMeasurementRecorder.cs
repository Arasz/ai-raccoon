namespace AiRaccoon.Core.Metrics;

/// <summary>
///     The port every measurement is recorded through. An implementation must never throw and
///     never block the caller (WP3) — recording is best-effort diagnostics, not a write the
///     caller's request depends on.
/// </summary>
public interface IMeasurementRecorder
{
    void Record(Measurement measurement);
}
