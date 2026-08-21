namespace AiRaccoon.Core.Memory.Code;

/// <summary>Code corpus search is always project-scoped (§3.1) — no scope/workspace parameter.</summary>
public sealed record CodeSearchQuery(string ProjectId, string Query, int Limit, double MinRelativeScore);
