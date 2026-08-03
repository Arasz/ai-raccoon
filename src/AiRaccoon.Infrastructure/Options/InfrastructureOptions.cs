using System.Runtime.InteropServices;

namespace AiRaccoon.Infrastructure.Options;

/// <summary>Where the install lives: a user-scope bank is shared by all projects, a project-scope bank belongs to one project (FR-MEM-1.3).</summary>
public enum InstallScope
{
    User,
    Project,
}

/// <summary>Library options; the caller builds these from IConfiguration or environment variables.</summary>
public sealed record InfrastructureOptions
{
    public string DataRoot { get; init; } = DefaultDataRoot();

    public string Rid { get; init; } = RuntimeInformation.RuntimeIdentifier;

    public InstallScope Scope { get; init; } = InstallScope.User;

    public SyncOptions Sync { get; init; } = new();

    /// <summary>Data root resolution: AIRACCOON_DATA_ROOT, else ~/.ai-raccoon (spec §5.1).</summary>
    public static string DefaultDataRoot()
    {
        var env = Environment.GetEnvironmentVariable("AIRACCOON_DATA_ROOT");
        return !string.IsNullOrWhiteSpace(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccoon");
    }
}
