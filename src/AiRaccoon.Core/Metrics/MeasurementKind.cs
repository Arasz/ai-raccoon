namespace AiRaccoon.Core.Metrics;

/// <summary>How a <see cref="Measurement" />'s Value should be interpreted.</summary>
public enum MeasurementKind
{
    /// <summary>A monotonically increasing count (e.g. a drop count).</summary>
    Counter,

    /// <summary>A distribution of observed values (e.g. a phase duration); percentiles apply.</summary>
    Histogram,

    /// <summary>A point-in-time reading (e.g. buffer occupancy at flush).</summary>
    Gauge
}
