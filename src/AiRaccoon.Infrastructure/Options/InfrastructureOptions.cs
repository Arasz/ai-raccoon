using System.Runtime.InteropServices;

namespace AiRaccoon.Infrastructure.Options;

/// <summary>
///     Where the install lives: a user-scope bank is shared by all projects, a project-scope bank belongs to one
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
    public string DataRoot { get; init; } = DefaultDataRoot();

    public string Rid { get; init; } = RuntimeInformation.RuntimeIdentifier;

    public InstallScope Scope { get; init; } = InstallScope.User;

    /// <summary>Data root fallback: ~/.ai-raccoon (see docs/work/features-agent-memory/spec-issue-1.md §5.1); the caller resolves env/CLI overrides.</summary>
    public static string DefaultDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccoon");
}
