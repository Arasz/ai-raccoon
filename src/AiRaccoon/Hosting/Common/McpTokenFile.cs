using System.Buffers.Text;
using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Options;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     The loopback secret guarding /mcp (docs/plans/2026-08-09-mcp-loopback-token-flow.md), moved
///     into the bank state directory by F49: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon
///     for project scope. Minted by `serve` before it binds; read — never minted — by the proxy. A
///     project root's legacy top-level token is validated and adopted once, then deleted. Only content
///     the mint could have written counts as a token.
/// </summary>
public sealed class McpTokenFile : OwnerOnlySecretFile<string>
{
    public const string FileName = "mcp-token";

    private const int TokenBytes = 32;

    /// <summary>What the mint produces, so a truncated write can never pass as a token.</summary>
    private static readonly int TokenLength = Base64Url.EncodeToString(new byte[TokenBytes]).Length;

    /// <summary>Its own gate: the lock file serializes across processes, which the create-only mint alone cannot (F7).</summary>
    private static readonly SemaphoreSlim TokenGate = new(1, 1);

    private readonly string? _legacyPath;

    /// <summary>User-scope root: the state directory is the root itself, exactly as before F49.</summary>
    public McpTokenFile(string dataRoot, TimeProvider? timeProvider = null, TimeSpan? healAfter = null)
        : this(UserScopeOptions(dataRoot), timeProvider, healAfter)
    {
    }

    /// <summary>Scope-aware: user scope keeps the token at the data root, project scope under .ai-raccoon.</summary>
    public McpTokenFile(InfrastructureOptions options, TimeProvider? timeProvider = null, TimeSpan? healAfter = null)
        : base(options, FileName, timeProvider, healAfter)
    {
        _legacyPath = options.Scope == InstallScope.Project
            ? System.IO.Path.Combine(options.DataRoot, FileName)
            : null;
    }

    protected override SemaphoreSlim Gate => TokenGate;

    protected override string Mint() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    protected override string Serialize(string secret) => secret;

    /// <summary>The token in <paramref name="content" />, or null when the mint could not have written it.</summary>
    protected override string? Parse(string content, out string? refusal)
    {
        refusal = null;
        var token = content.Trim();
        return token.Length == TokenLength ? token : null;
    }

    /// <summary>
    ///     When the state path is absent, falls back to a valid legacy top-level token — read-only,
    ///     never a write and never a mint at the legacy path.
    /// </summary>
    protected override string? ReadSecret()
    {
        var stateToken = ReadStateFile();
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
            return Parse(File.ReadAllText(_legacyPath), out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A project root's legacy top-level token is adopted before a mint; never mints at the legacy path.</summary>
    protected override async Task<string?> EnsureLockedAsync(CancellationToken cancellationToken)
    {
        // State-dir wins: an existing file (token or debris) is handled by the normal read/heal/mint;
        // the legacy path is consulted only when the state path does not exist at all.
        if (!File.Exists(Path) && _legacyPath is not null && File.Exists(_legacyPath))
        {
            if (!OwnerOnlyFile.IsFilePrivate(_legacyPath))
            {
                NotOwnerOnly = true;
                RefusalReason =
                    $"'{_legacyPath}' is not owner-only and may hold a secret — remove it, or run 'chmod 600 \"{_legacyPath}\"' and start again";
                return null;
            }

            if (await MigrateLegacyAsync(cancellationToken) is { } adopted)
            {
                return adopted;
            }

            if (RefusalReason is not null)
            {
                return null; // the legacy file was refused; fail closed rather than mint over it
            }
        }

        return await base.EnsureLockedAsync(cancellationToken);
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
            legacyToken = Parse(File.ReadAllText(_legacyPath!), out _) ??
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

        if (!await TryWriteNewAsync(legacyToken, cancellationToken))
        {
            return null; // the state path appeared first; it wins and the legacy file stays put
        }

        TryDeleteLegacy();
        return legacyToken;
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
