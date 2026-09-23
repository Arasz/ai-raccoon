namespace AiRaccoon.Core.Metrics;

/// <summary>One named metric value with its unit, as <see cref="AiRaccoon.Core.Memory.Fusion.FusionDiff.Measurements" /> reports it.</summary>
public readonly record struct GaugeReading(string Name, double Value, string Unit);
