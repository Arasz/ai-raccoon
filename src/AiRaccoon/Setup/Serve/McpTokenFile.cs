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

    private const int TokenBytes = 32;
    private const int Attempts = 20;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public McpTokenFile(string dataRoot)
    {
        Guard.IsNotNullOrWhiteSpace(dataRoot);
        Path = System.IO.Path.Combine(dataRoot, FileName);
    }

    /// <summary>Absolute path of the token file — the one thing an operator has to look at.</summary>
    public string Path { get; }

    /// <summary>The existing token, or a freshly minted one. Racing callers converge on one secret.</summary>
    public async Task<string> EnsureAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (Read() is { } existing)
            {
                return existing;
            }

            if (await TryMintAsync(cancellationToken) is { } minted)
            {
                return minted;
            }

            // Lost the exclusive create, or the winner has not finished writing: re-read.
            await Task.Delay(RetryDelay, cancellationToken);
        }

        // Reached only when the file exists yet holds no token for a full second — a crash between
        // create and write. Rotation is manual by design: delete it and start serve again.
        return Read() ?? ThrowHelper.ThrowInvalidOperationException<string>(
            $"ai-raccoon: {Path} exists but holds no token — delete it and start serve again");
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
