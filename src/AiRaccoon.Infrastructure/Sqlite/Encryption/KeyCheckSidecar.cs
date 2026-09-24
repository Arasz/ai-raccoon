using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

/// <summary>The key-check sidecar is not owner-only, or its content could not have come from a mint.</summary>
public sealed class KeyCheckViolation(string message) : Exception(message);

/// <summary>A bank's random id and the tag proving which key produced it.</summary>
public sealed record KeyCheckRecord(byte[] BankId, byte[] Tag);

/// <summary>
///     Tells a wrong key apart from a corrupt bank when SQLCipher answers SQLITE_NOTADB for both
///     (ADR-0111). <c>memory.db.keycheck</c> holds a random bank-id and
///     HKDF-SHA256(the raw bank key, info: domain ‖ bank-id) — the sidecar never carries the key
///     itself. 0600, refuse-not-chmod like the state-directory secrets (IdentityKeyFile/McpTokenFile);
///     writes are atomic (temp + rename).
/// </summary>
public sealed class KeyCheckSidecar
{
    public const string Suffix = ".keycheck";

    private const string Domain = "ai-raccoon-keycheck/v1";
    private const int BankIdLength = 16;
    private const int TagLength = 32; // SHA-256 output size
    private const int RecordLength = BankIdLength + TagLength;

    private static readonly byte[] DomainBytes = Encoding.UTF8.GetBytes(Domain);

    private static readonly UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly UnixFileMode SharedBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public KeyCheckSidecar(string bankPath)
    {
        Guard.IsNotNullOrWhiteSpace(bankPath);
        FilePath = PathFor(bankPath);
    }

    /// <summary>Absolute path of the sidecar — the one thing an operator has to look at.</summary>
    public string FilePath { get; }

    public static string PathFor(string bankPath) => $"{bankPath}{Suffix}";

    /// <summary>The stored record, or null when the sidecar does not exist.</summary>
    /// <exception cref="KeyCheckViolation">The file is group/other-readable, or its content is malformed.</exception>
    public KeyCheckRecord? Read()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        EnsurePrivate();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new KeyCheckViolation($"'{FilePath}' could not be read: {ex.Message}");
        }

        if (bytes.Length != RecordLength)
        {
            throw new KeyCheckViolation($"'{FilePath}' is not a valid key-check sidecar — remove it to let a new one be minted");
        }

        return new KeyCheckRecord(bytes[..BankIdLength], bytes[BankIdLength..]);
    }

    /// <summary>True when <paramref name="key" /> reproduces <paramref name="record" />'s tag.</summary>
    public static bool Verifies(KeyCheckRecord record, string key)
    {
        Guard.IsNotNull(record);
        Guard.IsNotNullOrEmpty(key);
        return CryptographicOperations.FixedTimeEquals(ComputeTag(record.BankId, key), record.Tag);
    }

    /// <summary>
    ///     Mints a fresh record for <paramref name="key" /> only when the sidecar is absent. A
    ///     concurrent winner — even a corrupt write — is left exactly as it is; this never overwrites.
    /// </summary>
    public void MintIfMissing(string key)
    {
        Guard.IsNotNullOrEmpty(key);
        if (File.Exists(FilePath))
        {
            return;
        }

        try
        {
            WriteOwnerOnly(FilePath, Record(key), FileMode.CreateNew);
        }
        catch (IOException)
        {
            // Lost the race to mint: another opener already created it — leave it in place.
        }
    }

    /// <summary>Rewrites the sidecar for <paramref name="key" />, atomically (temp + rename).</summary>
    /// <exception cref="KeyCheckViolation">An existing sidecar is group/other-readable — refused, never chmoded.</exception>
    public void Rewrite(string key)
    {
        Guard.IsNotNullOrEmpty(key);
        EnsurePrivate();

        var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        WriteOwnerOnly(tempPath, Record(key), FileMode.CreateNew);
        File.Move(tempPath, FilePath, true);
    }

    private static byte[] Record(string key)
    {
        var bankId = RandomNumberGenerator.GetBytes(BankIdLength);
        return [.. bankId, .. ComputeTag(bankId, key)];
    }

    /// <summary>
    ///     One platform-KDF call, not a hand-assembled derive-then-HMAC: HKDF's own construction is
    ///     already HMAC-based, so folding the bank-id into <c>info</c> alongside the domain binds the
    ///     tag to (key, domain, bank-id) in a single audited primitive — no raw <c>HMACSHA256</c> call
    ///     needed on top (the no-hand-rolled-crypto gate reserves that shape for applying a key
    ///     already produced by a separate HKDF step, e.g. <c>SyncBlobAuthenticator</c>).
    /// </summary>
    private static byte[] ComputeTag(byte[] bankId, string key)
    {
        byte[] info = [.. DomainBytes, .. bankId];
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(key), TagLength, info: info);
    }

    private static void WriteOwnerOnly(string path, byte[] content, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFileMode;
        }

        using var stream = new FileStream(path, options);
        stream.Write(content);
    }

    private void EnsurePrivate()
    {
        if (OperatingSystem.IsWindows() || !File.Exists(FilePath))
        {
            return;
        }

        if ((File.GetUnixFileMode(FilePath) & SharedBits) != 0)
        {
            throw new KeyCheckViolation(
                $"'{FilePath}' is not owner-only and may hold key-check material — remove it, or run 'chmod 600 \"{FilePath}\"' and start again");
        }
    }
}
