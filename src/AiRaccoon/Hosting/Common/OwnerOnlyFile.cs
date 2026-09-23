using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     Owner-only enforcement and the cross-process lock shared by the state-directory secrets. POSIX
///     modes only: on Windows the file inherits the data-root ACL (ADR-0020 non-goals). A pre-existing
///     file another principal can read or write, or a directory another principal can write or owns,
///     is treated as planted and refuses; an owned directory others can only read is tightened.
/// </summary>
internal static partial class OwnerOnlyFile
{
    internal const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Any bit a group or other principal may hold: its presence means the path leaks.</summary>
    private const UnixFileMode SharedBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>The shared bits that let another principal plant or replace a file; never tightened away, always refused.</summary>
    private const UnixFileMode SharedWriteBits = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The advisory lock beside a secret; held with <see cref="FileShare.None" /> for mint and heal.</summary>
    internal static string LockPathFor(string secretPath) => secretPath + ".lock";

    /// <summary>
    ///     Creates the state directory owner-only. An existing one this user owns whose only leak is
    ///     group/world read or execute (an earlier binary's umask) is tightened to owner-only and true
    ///     returned; one others can write, or another user owns, is refused (ADR-0106 D1).
    /// </summary>
    internal static bool EnsureDirectory(string directory)
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

            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        var mode = File.GetUnixFileMode(directory);
        if ((mode & SharedBits) == 0)
        {
            return false;
        }

        if ((mode & SharedWriteBits) != 0)
        {
            throw new OwnerOnlyViolation(
                $"the state directory '{directory}' is not owner-only — run 'chmod 700 \"{directory}\"' and start again");
        }

        try
        {
            File.SetUnixFileMode(directory, mode & ~SharedBits);
        }
        catch (UnauthorizedAccessException)
        {
            throw new OwnerOnlyViolation(
                $"the state directory '{directory}' is not owner-only and another user owns it — have its owner run 'chmod 700 \"{directory}\"', or pass a --data-root you own");
        }

        return true;
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

    internal static partial class Log
    {
        [LoggerMessage(EventId = 692, Level = LogLevel.Information,
            Message = "ai-raccoon: the state directory {Directory} was readable by other users and was tightened to owner-only (0700)")]
        public static partial void StateDirectoryTightened(ILogger logger, string directory);
    }
}

/// <summary>A state-directory path a mint or read refuses to trust; the message names the remedy.</summary>
internal sealed class OwnerOnlyViolation(string message) : Exception(message);
