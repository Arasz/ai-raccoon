using System.Runtime.InteropServices;

namespace AiRaccoon.Infrastructure.Options;

/// <summary>
///     Where the install lives: a user-scope bank is shared by all projects, a project-scope bank belongs to one
///     project (FR-MEM-1.3).
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

    public SyncOptions Sync { get; init; } = new();

    /// <summary>Global access-mode seed (ro|rw|full); null = no seed (validated by AccessModePolicy at seed time).</summary>
    public string? AccessMode { get; init; }

    /// <summary>Custom ONNX embedding model path; null = the bundled model.</summary>
    public string? EmbeddingModelPath { get; init; }

    /// <summary>Data root fallback: ~/.ai-raccoon (spec §5.1); the caller resolves env/CLI overrides.</summary>
    public static string DefaultDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccoon");
}
