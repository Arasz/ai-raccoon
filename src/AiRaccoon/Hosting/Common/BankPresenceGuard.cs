using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Common;

/// <summary>Raised by <see cref="BankPresenceGuard" /> before any probe or spawn; the message names the resolved bank path and the remedy.</summary>
internal sealed class BankMissingException(string message) : Exception(message);

/// <summary>
///     F39: a client auto-launch (the proxy's private spawn, <see cref="AiRaccoon.Hosting.Proxy.BackendSessions" />;
///     a settings verb's shared attach-or-start, <see cref="AiRaccoon.Settings.CliSettingsBackend" />)
///     must never mint a bank at a mistyped <c>--data-root</c>. The default root keeps today's
///     bootstrap unconditionally (path identity, not whether the flag was passed); any other root
///     with no bank file yet is refused before the caller ever probes a port or spawns a process.
///     <c>serve</c>, <c>encryption</c> and <c>doctor</c> never call this — they open or create the
///     bank themselves, so they stay exempt structurally, without a flag.
/// </summary>
internal static class BankPresenceGuard
{
    public static void EnsureExists(InfrastructureOptions options)
    {
        Guard.IsNotNull(options);
        if (IsDefaultDataRoot(options.DataRoot))
        {
            return;
        }

        var bankPath = SqliteConnectionFactory.BankPathFor(options);
        if (File.Exists(bankPath))
        {
            return;
        }

        throw new BankMissingException(
            $"ai-raccoon: no bank exists at '{bankPath}' — create it with 'ai-raccoon serve --data-root {options.DataRoot}', or check --data-root for a typo");
    }

    /// <summary>Path identity, not "was --data-root passed": compared after expansion so a trailing
    /// separator or a "go up and back down" spelling of the default root still exempts it.</summary>
    private static bool IsDefaultDataRoot(string dataRoot) =>
        string.Equals(NormalizedFullPath(dataRoot), NormalizedFullPath(DefaultOptions.DataRoot), PathComparison);

    private static string NormalizedFullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
