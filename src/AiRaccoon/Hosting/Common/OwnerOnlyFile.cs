using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     Owner-only enforcement and the cross-process lock shared by the state-directory secrets. POSIX
///     modes only: on Windows the file inherits the data-root ACL (ADR-0020 non-goals). A pre-existing
///     directory or file another principal can read or write is treated as planted and refuses.
/// </summary>
internal static class OwnerOnlyFile
{
    internal const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Any bit a group or other principal may hold: its presence means the path leaks.</summary>
    private const UnixFileMode SharedBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The advisory lock beside a secret; held with <see cref="FileShare.None" /> for mint and heal.</summary>
    internal static string LockPathFor(string secretPath) => secretPath + ".lock";

    /// <summary>
    ///     Creates the state directory owner-only, then refuses a pre-existing one that is shared —
    ///     another principal could replace the trust anchor in it (F2/D1).
    /// </summary>
    internal static void EnsureDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, OwnerDirectoryMode);
            }

            return;
        }

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) & SharedBits) != 0)
        {
            throw new OwnerOnlyViolation(
                $"the state directory '{directory}' is not owner-only — run 'chmod 700 \"{directory}\"' and start again");
        }
    }

    /// <summary>Refuses an existing secret file that a group or other principal can read or write.</summary>
    internal static void EnsureFileIsPrivate(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        if ((File.GetUnixFileMode(path) & SharedBits) != 0)
        {
            throw new OwnerOnlyViolation(
                $"'{path}' is not owner-only and may hold a secret — remove it, or run 'chmod 600 \"{path}\"' and start again");
        }
    }

    /// <summary>Never throws: false means the path is shared, unreadable, or cannot be inspected.</summary>
    internal static bool IsDirectoryPrivate(string directory)
    {
        try
        {
            return OperatingSystem.IsWindows() || !Directory.Exists(directory) ||
                   (File.GetUnixFileMode(directory) & SharedBits) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Never throws: false means the file is shared, unreadable, or cannot be inspected.</summary>
    internal static bool IsFilePrivate(string path)
    {
        try
        {
            EnsureFileIsPrivate(path);
            return true;
        }
        catch (Exception ex) when (ex is OwnerOnlyViolation or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     True when <paramref name="path" /> has not been written for at least the heal window — the
    ///     guard against deleting a file a live writer (possibly an older binary) just created.
    /// </summary>
    internal static bool OldEnoughToDelete(string path, TimeProvider timeProvider, TimeSpan healAfter)
    {
        try
        {
            return timeProvider.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(path) >= healAfter;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Takes the OS lock serialising mint and heal across processes. A contended lock is retried;
    ///     cancellation ends the wait, and any other failure (an unwritable directory) propagates.
    /// </summary>
    internal static async Task<FileStream> AcquireLockAsync(string secretPath, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNullOrWhiteSpace(secretPath);

        var lockPath = LockPathFor(secretPath);
        while (true)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = OwnerFileMode;
                }

                return new FileStream(lockPath, options);
            }
            catch (IOException)
            {
                // Another process holds it; FileShare.None is enforced by the OS on Unix, not just in-process.
                await Task.Delay(PollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>A state-directory path a mint or read refuses to trust; the message names the remedy.</summary>
internal sealed class OwnerOnlyViolation(string message) : Exception(message);
