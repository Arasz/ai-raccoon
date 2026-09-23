namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>The entries-table bucket columns (scope/project/label/workspace) a context string maps to.</summary>
internal readonly record struct BucketColumns(string? Scope, string ProjectId, string? ContextLabel, string? WorkspaceId);
