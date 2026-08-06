using System.Runtime.InteropServices;

namespace AiRaccoon.Infrastructure.Options;

/// <summary>
///     Where the installation lives: a user-scope bank is shared by all projects, a project-scope bank belongs to one
///     project (see docs/work/features-agent-memory/spec-issue-1.md, FR-MEM-1.3).
/// </summary>
public enum InstallScope
{
    User,
    Project
}

/// <summary>Library options; the caller builds these from IConfiguration or environment variables.</summary>
public sealed record InfrastructureOptions
{
    public required string DataRoot { get; init; }

    public string Rid { get; init; } = RuntimeInformation.RuntimeIdentifier;

    public required InstallScope Scope { get; init; }

    /// <summary>When true, each tool call prints its one-word status to stderr as it starts.</summary>
    public bool StatusWords { get; init; }

    /// <summary>Append-only JSONL operation log path (from the AIRACCOON_MEMORY_LOG env var); null = off.</summary>
    public string? MemoryLogPath { get; init; }
}
