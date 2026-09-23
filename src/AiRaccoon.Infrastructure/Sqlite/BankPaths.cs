using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     The bank's state directory (F49): user scope keeps it at the data root; a project-scope
///     install nests it in &lt;dataRoot&gt;/.ai-raccoon so the bank, the token and the identity key
///     travel together and never sit at a project root's top level.
/// </summary>
public static class BankPaths
{
    /// <summary>
    ///     Directory holding the bank and its secrets: the data root for user scope,
    ///     &lt;dataRoot&gt;/.ai-raccoon for project scope.
    /// </summary>
    public static string DirectoryFor(InfrastructureOptions options) =>
        options.Scope switch
        {
            InstallScope.User => options.DataRoot,
            InstallScope.Project => Path.Combine(options.DataRoot, ".ai-raccoon"),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Scope), options.Scope, "Unknown install scope.")
        };

    /// <summary>
    ///     Creates a missing state directory owner-only (0700 on POSIX), whichever process gets there
    ///     first; an existing directory is left exactly as it is.
    /// </summary>
    public static void CreateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
