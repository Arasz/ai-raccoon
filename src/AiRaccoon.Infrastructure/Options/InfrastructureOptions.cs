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

    /// <summary>When true, every log level is written to a file beside the bank instead of the
    /// console — nothing reaches stdout/stderr (owner ruling 2026-08-09).</summary>
    public bool Quiet { get; init; }
}
