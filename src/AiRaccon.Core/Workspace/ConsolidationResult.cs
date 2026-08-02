namespace AiRaccon.Core.Workspace;

/// <summary>Outcome of promoting a workspace's outbox into committed project memory (spec FR-MEM-1.6).</summary>
public sealed record ConsolidationResult(int Promoted, int Discarded);
