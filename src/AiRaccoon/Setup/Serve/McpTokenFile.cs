using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     The loopback secret guarding /mcp, kept in a 0600 file under the data root
///     (docs/plans/2026-08-09-mcp-loopback-token-flow.md). Minted by `serve` before it binds;
///     read — never minted — by the proxy.
/// </summary>
internal sealed class McpTokenFile
{
    public const string FileName = "mcp-token";

    /// <summary>How long an empty file is given to fill in before it counts as debris. Well inside
    /// BackendLauncher's acquire budget, so the heal rescues the start that hit the problem.</summary>
    public static readonly TimeSpan HealAfter = TimeSpan.FromSeconds(5);

    private const int TokenBytes = 32;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly TimeSpan _healAfter;
    private readonly TimeProvider _timeProvider;

    public McpTokenFile(string dataRoot, TimeProvider? timeProvider = null, TimeSpan? healAfter = null)
    {
        Guard.IsNotNullOrWhiteSpace(dataRoot);
        Path = System.IO.Path.Combine(dataRoot, FileName);
        _timeProvider = timeProvider ?? TimeProvider.System; // test seam: fake clock for the wait
        _healAfter = healAfter ?? HealAfter;
        Guard.IsGreaterThan(_healAfter, TimeSpan.Zero);
    }

    /// <summary>Absolute path of the token file — the one thing an operator has to look at.</summary>
    public string Path { get; }

    /// <summary>The token, or null when it can be neither read nor minted. Racing callers converge
    /// on one secret, and debris left by a crash is healed rather than wedging every later start.</summary>
    public async Task<string?> EnsureAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        if (await AcquireAsync(cancellationToken) is { } token)
        {
            return token;
        }

        // The file held nothing for the whole wait, so whoever created it died between the
        // exclusive create and the write. Remove it and mint through that same exclusive create —
        // never by writing over the file — so concurrent healers converge as concurrent minters do.
        TryDeleteWhileEmpty();
        return await AcquireAsync(cancellationToken);
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
                if (Read() is { } existing)
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
        catch (OperationCanceledException) when (waited.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The wait expired, not the caller's token: report the miss rather than throwing.
        }

        return null;
    }

    /// <summary>Deletes the file only while it is genuinely empty; a live writer or a filled file wins.</summary>
    internal bool TryDeleteWhileEmpty()
    {
        try
        {
            // FileShare.None, not Read(): a mint in flight holds the file exclusively, and Read()
            // reports that as absent — which would delete the token the winner is still writing.
            using (var held = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (held.Length > 0)
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

    /// <summary>The stored token, or null when the file is missing, empty or unreadable.</summary>
    public string? Read()
    {
        try
        {
            var token = File.ReadAllText(Path).Trim();
            return token.Length == 0 ? null : token;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A newly minted token, or null when the file already exists or cannot be created.</summary>
    internal async Task<string?> TryMintAsync(CancellationToken cancellationToken)
    {
        var minted = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            // POSIX-only by design; on Windows the file inherits the data-root ACL (ADR-0020 non-goals).
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using var stream = new FileStream(Path, options);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(minted), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return minted;
    }
}
