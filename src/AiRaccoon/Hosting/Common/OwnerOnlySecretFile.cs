using System.Text;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     A secret kept as one owner-only file in the bank state directory: minted through an exclusive
///     create under a process gate and a cross-process lock, read only while private, and healed when
///     a crash left debris older than the heal window.
/// </summary>
public abstract class OwnerOnlySecretFile<T> where T : class
{
    /// <summary>How long a file holding no secret is given to fill in before it counts as a crash's debris.</summary>
    public static readonly TimeSpan HealAfter = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly TimeSpan _healAfter;

    protected OwnerOnlySecretFile(InfrastructureOptions options, string fileName, TimeProvider? timeProvider, TimeSpan? healAfter)
    {
        Guard.IsNotNull(options);
        Guard.IsNotNullOrWhiteSpace(fileName);
        StateDirectory = BankPaths.DirectoryFor(options);
        Path = System.IO.Path.Combine(StateDirectory, fileName);
        TimeProvider = timeProvider ?? TimeProvider.System;
        _healAfter = healAfter ?? HealAfter;
        Guard.IsGreaterThan(_healAfter, TimeSpan.Zero);
    }

    /// <summary>Directory holding the secret: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon for project scope.</summary>
    public string StateDirectory { get; }

    /// <summary>Absolute path of the secret file — the one thing an operator has to look at.</summary>
    public string Path { get; }

    /// <summary>Why the last ensure or read refused, with the remedy; null when it did not.</summary>
    public string? RefusalReason { get; protected set; }

    /// <summary>True when the last ensure refused because the state directory or a secret file is not owner-only.</summary>
    public bool NotOwnerOnly { get; protected set; }

    /// <summary>True when the last ensure tightened an owned state directory others could only read to 0700.</summary>
    public bool TightenedStateDirectory { get; private set; }

    protected TimeProvider TimeProvider { get; }

    /// <summary>Serializes mint and heal across callers in this process; the lock file does so across processes.</summary>
    protected abstract SemaphoreSlim Gate { get; }

    /// <summary>
    ///     The secret, minted or healed when needed; null when the state directory or an existing file
    ///     is not owner-only, or the file cannot be written. Racing callers converge on one secret.
    /// </summary>
    public async Task<T?> EnsureAsync(CancellationToken cancellationToken)
    {
        RefusalReason = null;
        NotOwnerOnly = false;
        TightenedStateDirectory = false;
        try
        {
            TightenedStateDirectory = OwnerOnlyFile.EnsureDirectory(StateDirectory);
            await Gate.WaitAsync(cancellationToken);
            try
            {
                await using var held = await OwnerOnlyFile
                    .AcquireLockAsync(Path, TimeProvider, cancellationToken);
                var ensured = await EnsureLockedAsync(cancellationToken);
                if (ensured is not null)
                {
                    RefusalReason = null; // a refused read that a later mint healed is not a refusal anymore
                }

                return ensured;
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (OwnerOnlyViolation ex)
        {
            NotOwnerOnly = true;
            RefusalReason = ex.Message;
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            RefusalReason = $"'{StateDirectory}' cannot be written: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    ///     The stored secret, or null when it is missing, unreadable, malformed, or shared. Read-only by
    ///     construction: no directory, lock file or secret file is ever created here.
    /// </summary>
    public T? Read()
    {
        RefusalReason = null;
        if (!OwnerOnlyFile.IsDirectoryPrivate(StateDirectory))
        {
            RefusalReason =
                $"the state directory '{StateDirectory}' is not owner-only — run 'chmod 700 \"{StateDirectory}\"' and start again";
            return null;
        }

        return ReadSecret();
    }

    /// <summary>A newly minted secret, or null when the file already exists or cannot be created.</summary>
    internal async Task<T?> TryMintAsync(CancellationToken cancellationToken)
    {
        var fresh = Mint();
        var written = false;
        try
        {
            written = await TryWriteNewAsync(Serialize(fresh), cancellationToken);
        }
        finally
        {
            if (!written)
            {
                (fresh as IDisposable)?.Dispose();
            }
        }

        if (!written)
        {
            return null;
        }

        Minted(fresh);
        return fresh;
    }

    /// <summary>Deletes the file only while it holds no secret and is older than the heal window.</summary>
    internal bool TryDeleteDebris()
    {
        try
        {
            // FileShare.None, not Read(): a mint in flight holds the file exclusively, and Read()
            // reports that as absent — which would delete the secret the winner is still writing.
            using (var held = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using var reader = new StreamReader(held, Encoding.UTF8);
                if (Parse(reader.ReadToEnd(), out _) is { } probe)
                {
                    (probe as IDisposable)?.Dispose();
                    return false;
                }

                if (!OwnerOnlyFile.OldEnoughToDelete(Path, TimeProvider, _healAfter))
                {
                    return false;
                }
            }

            File.Delete(Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A new secret; written only if the exclusive create wins.</summary>
    protected abstract T Mint();

    /// <summary>The secret in <paramref name="content" />, or null — with a refusal when one names a remedy — when it holds none.</summary>
    protected abstract T? Parse(string content, out string? refusal);

    /// <summary>The file content a mint writes for <paramref name="secret" />.</summary>
    protected abstract string Serialize(T secret);

    /// <summary>Called once a mint's exclusive create landed.</summary>
    protected virtual void Minted(T secret)
    {
    }

    /// <summary>What <see cref="Read" /> returns once the state directory is known to be private.</summary>
    protected virtual T? ReadSecret() => ReadStateFile();

    /// <summary>Reads, mints, or heals; runs under the gate and the lock file.</summary>
    protected virtual async Task<T?> EnsureLockedAsync(CancellationToken cancellationToken)
    {
        if (await AcquireAsync(cancellationToken) is { } acquired)
        {
            return acquired;
        }

        if (!TryDeleteDebris())
        {
            // A live writer may have finished while the heal wait ran; only the parse decides.
            return ReadStateFile();
        }

        return await AcquireAsync(cancellationToken);
    }

    /// <summary>The secret at <see cref="Path" />, or null when it is missing, unreadable, malformed, or shared.</summary>
    protected virtual T? ReadStateFile()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            OwnerOnlyFile.EnsureFileIsPrivate(Path);
            var secret = Parse(File.ReadAllText(Path), out var refusal);
            RefusalReason = refusal;
            return secret;
        }
        catch (OwnerOnlyViolation ex)
        {
            NotOwnerOnly = true;
            RefusalReason = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="content" /> through the exclusive create; false means the path already existed.</summary>
    protected async Task<bool> TryWriteNewAsync(string content, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            // POSIX-only by design; on Windows the file inherits the data-root ACL (ADR-0020 non-goals).
            options.UnixCreateMode = OwnerOnlyFile.OwnerFileMode;
        }

        try
        {
            await using var stream = new FileStream(Path, options);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads or mints, retrying until the heal wait expires.</summary>
    private async Task<T?> AcquireAsync(CancellationToken cancellationToken)
    {
        using var waited = new CancellationTokenSource(_healAfter, TimeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, waited.Token);
        using var timer = new PeriodicTimer(PollInterval, TimeProvider);
        try
        {
            do
            {
                if (ReadStateFile() is { } existing)
                {
                    return existing;
                }

                if (await TryMintAsync(cancellationToken) is { } minted)
                {
                    return minted;
                }
            }
            // Lost the exclusive create, or the winner has not finished writing: re-read.
            while (await timer.WaitForNextTickAsync(waiting.Token));
        }
        catch (OperationCanceledException)
            when (waited.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The wait expired, not the caller's token: report the miss rather than throwing.
        }

        return null;
    }
}
