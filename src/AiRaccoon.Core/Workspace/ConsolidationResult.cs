namespace AiRaccoon.Core.Workspace;

/// <summary>Outcome of promoting a workspace's outbox into committed project memory (see docs/work/features-agent-memory/spec-issue-1.md, FR-MEM-1.6).</summary>
public sealed record ConsolidationResult(int Promoted, int Discarded);
