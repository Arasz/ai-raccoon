using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     The loopback secret guarding /mcp (docs/plans/2026-08-09-mcp-loopback-token-flow.md), moved
///     into the bank state directory by F49: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon
///     for project scope. Minted by `serve` before it binds; read — never minted — by the proxy. A
///     project root's legacy top-level token is validated and adopted once, then deleted. Only content
///     the mint could have written counts as a token.
/// </summary>
public sealed class McpTokenFile
{
    public const string FileName = "mcp-token";

    private const int TokenBytes = 32;

    /// <summary>
    ///     How long a file holding no token is given to fill in before it counts as debris. Well
    ///     inside BackendLauncher's acquire budget, so the heal rescues the start that hit the problem.
    /// </summary>
    public static readonly TimeSpan HealAfter = TimeSpan.FromSeconds(5);

    /// <summary>What the mint produces, so a truncated write can never pass as a token.</summary>
    private static readonly int TokenLength = Base64Url.EncodeToString(new byte[TokenBytes]).Length;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Serializes ensure and heal across concurrent callers in this process; the lock file
    /// does the same across processes, which the create-only mint alone cannot (F7).</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TimeSpan _healAfter;
    private readonly string? _legacyPath;
    private readonly TimeProvider _timeProvider;

    /// <summary>User-scope root: the state directory is the root itself, exactly as before F49.</summary>
    public McpTokenFile(string dataRoot, TimeProvider? timeProvider = null, TimeSpan? healAfter = null)
        : this(UserScopeOptions(dataRoot), timeProvider, healAfter)
    {
    }

    /// <summary>Scope-aware: user scope keeps the token at the data root, project scope under .ai-raccoon.</summary>
    public McpTokenFile(InfrastructureOptions options, TimeProvider? timeProvider = null, TimeSpan? healAfter = null)
    {
        Guard.IsNotNull(options);
        StateDirectory = BankPaths.DirectoryFor(options);
        Path = System.IO.Path.Combine(StateDirectory, FileName);
        _legacyPath = options.Scope == InstallScope.Project
            ? System.IO.Path.Combine(options.DataRoot, FileName)
            : null;
        _timeProvider = timeProvider ?? TimeProvider.System; // test seam: fake clock for the wait
        _healAfter = healAfter ?? HealAfter;
        Guard.IsGreaterThan(_healAfter, TimeSpan.Zero);
    }

    /// <summary>Directory holding the token: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon for project scope.</summary>
    public string StateDirectory { get; }

    /// <summary>Absolute path of the token file — the one thing an operator has to look at.</summary>
    public string Path { get; }

    /// <summary>Why the last ensure or read refused, with the remedy; null when it did not.</summary>
    public string? RefusalReason { get; private set; }

    /// <summary>True when the last ensure tightened an owned state directory others could only read to 0700.</summary>
    public bool TightenedStateDirectory { get; private set; }

    /// <summary>
    ///     The token, or null when it can be neither read nor minted. Racing callers converge on one
    ///     secret, debris left by a crash is healed rather than wedging every later start, and a
    ///     project root's legacy top-level token is adopted before a mint. Never mints at the legacy path.
    /// </summary>
    public async Task<string?> EnsureAsync(CancellationToken cancellationToken)
    {
        RefusalReason = null;
        TightenedStateDirectory = false;
        try
        {
            TightenedStateDirectory = OwnerOnlyFile.EnsureDirectory(StateDirectory);
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var held = await OwnerOnlyFile
                    .AcquireLockAsync(Path, _timeProvider, cancellationToken).ConfigureAwait(false);
                var ensured = await EnsureLockedAsync(cancellationToken).ConfigureAwait(false);
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
    ///     The stored token, or null when it is missing, unreadable, malformed, or shared. When the
    ///     state path is absent it falls back to a valid legacy top-level token — read-only, never a
    ///     write and never a mint at the legacy path.
    /// </summary>
    public string? Read()
    {
        RefusalReason = null;
        if (!OwnerOnlyFile.IsDirectoryPrivate(StateDirectory))
        {
            RefusalReason =
                $"the state directory '{StateDirectory}' is not owner-only — run 'chmod 700 \"{StateDirectory}\"' and start again";
            return null;
        }

        var stateToken = File.Exists(Path) ? ReadStateToken() : null;
        if (stateToken is not null || File.Exists(Path) || _legacyPath is null || !File.Exists(_legacyPath))
        {
            return stateToken;
        }

        if (!OwnerOnlyFile.IsFilePrivate(_legacyPath))
        {
            RefusalReason =
                $"'{_legacyPath}' is not owner-only and may hold a secret — remove it, or run 'chmod 600 \"{_legacyPath}\"' and start again";
            return null;
        }

        try
        {
            return Parse(File.ReadAllText(_legacyPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the file only while it holds no token and is older than the heal window.</summary>
    internal bool TryDeleteDebris()
    {
        try
        {
            // FileShare.None, not Read(): a mint in flight holds the file exclusively, and Read()
            // reports that as absent — which would delete the token the winner is still writing.
            using (var held = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using var reader = new StreamReader(held, Encoding.UTF8);
                if (Parse(reader.ReadToEnd()) is not null)
                {
                    return false;
                }

                if (!OwnerOnlyFile.OldEnoughToDelete(Path, _timeProvider, _healAfter))
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

    /// <summary>A newly minted token, or null when the file already exists or cannot be created.</summary>
    internal async Task<string?> TryMintAsync(CancellationToken cancellationToken)
    {
        var minted = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        return await TryWriteNewAsync(minted, cancellationToken).ConfigureAwait(false) ? minted : null;
    }

    private async Task<string?> EnsureLockedAsync(CancellationToken cancellationToken)
    {
        // State-dir wins: an existing file (token or debris) is handled by the normal read/heal/mint;
        // the legacy path is consulted only when the state path does not exist at all.
        if (!File.Exists(Path) && _legacyPath is not null && File.Exists(_legacyPath))
        {
            if (!OwnerOnlyFile.IsFilePrivate(_legacyPath))
            {
                RefusalReason =
                    $"'{_legacyPath}' is not owner-only and may hold a secret — remove it, or run 'chmod 600 \"{_legacyPath}\"' and start again";
                return null;
            }

            if (await MigrateLegacyAsync(cancellationToken).ConfigureAwait(false) is { } adopted)
            {
                return adopted;
            }

            if (RefusalReason is not null)
            {
                return null; // the legacy file was refused; fail closed rather than mint over it
            }
        }

        if (await AcquireAsync(cancellationToken).ConfigureAwait(false) is { } token)
        {
            return token;
        }

        if (!TryDeleteDebris())
        {
            // A live writer may have finished during the wait; only the parse decides.
            return ReadStateToken();
        }

        return await AcquireAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adopts a valid legacy token through the exclusive create, then deletes the legacy file only
    ///     after that write landed. Null means the state path won the race, or the legacy file held
    ///     nothing a mint could have written — in both cases the caller re-reads rather than guesses.
    /// </summary>
    private async Task<string?> MigrateLegacyAsync(CancellationToken cancellationToken)
    {
        string legacyToken;
        try
        {
            // A legacy file that exists must hold either a token or be refused; a truncated write
            // is debris, and adopting it would lower the secret's entropy silently.
            legacyToken = Parse(File.ReadAllText(_legacyPath!)) ??
                          throw new OwnerOnlyViolation(
                              $"'{_legacyPath}' does not hold a token — remove it, or restore the token, and start again");
        }
        catch (OwnerOnlyViolation ex)
        {
            RefusalReason = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RefusalReason = $"'{_legacyPath}' cannot be read: {ex.Message}";
            return null;
        }

        if (!await TryWriteNewAsync(legacyToken, cancellationToken).ConfigureAwait(false))
        {
            return null; // the state path appeared first; it wins and the legacy file stays put
        }

        TryDeleteLegacy();
        return legacyToken;
    }

    /// <summary>Reads or mints, retrying until the heal wait expires.</summary>
    private async Task<string?> AcquireAsync(CancellationToken cancellationToken)
    {
        using var waited = new CancellationTokenSource(_healAfter, _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, waited.Token);
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        try
        {
            do
            {
                if (ReadStateToken() is { } existing)
                {
                    return existing;
                }

                if (await TryMintAsync(cancellationToken).ConfigureAwait(false) is { } minted)
                {
                    return minted;
                }
            }
            // Lost the exclusive create, or the winner has not finished writing: re-read.
            while (await timer.WaitForNextTickAsync(waiting.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
            when (waited.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The wait expired, not the caller's token: report the miss rather than throwing.
        }

        return null;
    }

    /// <summary>The token in <paramref name="content" />, or null when the mint could not have written it.</summary>
    private static string? Parse(string content)
    {
        var token = content.Trim();
        return token.Length == TokenLength ? token : null;
    }

    /// <summary>Writes <paramref name="content" /> through the exclusive create; false means the path already existed.</summary>
    private async Task<bool> TryWriteNewAsync(string content, CancellationToken cancellationToken)
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
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? ReadStateToken()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            OwnerOnlyFile.EnsureFileIsPrivate(Path);
            return Parse(File.ReadAllText(Path));
        }
        catch (OwnerOnlyViolation ex)
        {
            RefusalReason = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryDeleteLegacy()
    {
        try
        {
            File.Delete(_legacyPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The state-dir write already won; a legacy file the process cannot delete is a
            // stale duplicate, not a reason to fail the start.
        }
    }

    private static InfrastructureOptions UserScopeOptions(string dataRoot)
    {
        Guard.IsNotNullOrWhiteSpace(dataRoot);
        return new InfrastructureOptions { DataRoot = dataRoot, Scope = InstallScope.User };
    }
}
