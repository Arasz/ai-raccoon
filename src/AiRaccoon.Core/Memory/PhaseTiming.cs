namespace AiRaccoon.Core.Memory;

/// <summary>One named phase's measured duration, as <see cref="SearchTimings.Phases" /> and <see cref="SearchTimings.Measurements" /> report it.</summary>
public readonly record struct PhaseTiming(string Name, TimeSpan Value);
