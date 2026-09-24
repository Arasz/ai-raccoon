using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

/// <summary>The key-check sidecar is not owner-only, or its content could not have come from a mint.</summary>
public sealed class KeyCheckViolation(string message) : Exception(message);

/// <summary>A bank's random id, the PBKDF2 salt and work factor the tag was computed with, and the tag itself.</summary>
public sealed record KeyCheckRecord(byte[] BankId, byte[] Salt, int Iterations, byte[] Tag);

/// <summary>
///     Tells a wrong key apart from a corrupt bank when SQLCipher answers SQLITE_NOTADB for both
///     (ADR-0111). <c>memory.db.keycheck</c> holds a random bank-id and
///     HKDF-SHA256(the tag input, info: domain ‖ bank-id) — the sidecar never carries the key
///     itself. A human-typed passphrase is pre-stretched with PBKDF2 (a per-bank random salt, at
///     least SQLCipher's own work factor) before that HKDF step, so a leaked sidecar cannot become
///     an HMAC-speed dictionary oracle on it; a raw SQLCipher key literal (<c>x'...'</c>) is already
///     32 high-entropy bytes with nothing to dictionary-attack, so it skips the stretch. 0600,
///     refuse-not-chmod like the state-directory secrets (IdentityKeyFile/McpTokenFile); writes are
///     atomic (temp + rename).
/// </summary>
public sealed class KeyCheckSidecar
{
    public const string Suffix = ".keycheck";

    /// <summary>SQLCipher's own default PBKDF2 work factor (<c>PRAGMA kdf_iter</c>) — the floor a passphrase-shaped key is stretched to.</summary>
    public const int WorkFactorIterations = 256_000;

    private const string Domain = "ai-raccoon-keycheck/v1";
    private const int BankIdLength = 16;
    private const int SaltLength = 16;
    private const int IterationsLength = sizeof(int);
    private const int TagLength = 32; // SHA-256 output size
    private const int RecordLength = BankIdLength + SaltLength + IterationsLength + TagLength;

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

        var bankId = bytes[..BankIdLength];
        var salt = bytes[BankIdLength..(BankIdLength + SaltLength)];
        var iterations = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(BankIdLength + SaltLength, IterationsLength));
        if (iterations < 0)
        {
            throw new KeyCheckViolation($"'{FilePath}' is not a valid key-check sidecar — remove it to let a new one be minted");
        }

        var tag = bytes[(BankIdLength + SaltLength + IterationsLength)..];
        return new KeyCheckRecord(bankId, salt, iterations, tag);
    }

    /// <summary>True when <paramref name="key" /> reproduces <paramref name="record" />'s tag.</summary>
    public static bool Verifies(KeyCheckRecord record, string key)
    {
        Guard.IsNotNull(record);
        Guard.IsNotNullOrEmpty(key);
        return CryptographicOperations.FixedTimeEquals(ComputeTag(record.BankId, record.Salt, record.Iterations, key), record.Tag);
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
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var iterations = IsRawKeyLiteral(key) ? 0 : WorkFactorIterations;

        var iterationsBytes = new byte[IterationsLength];
        BinaryPrimitives.WriteInt32BigEndian(iterationsBytes, iterations);

        return [.. bankId, .. salt, .. iterationsBytes, .. ComputeTag(bankId, salt, iterations, key)];
    }

    /// <summary>
    ///     A human-typed passphrase is pre-stretched with PBKDF2 (<paramref name="iterations" /> ≥ 0,
    ///     stored in the record so a later work-factor increase never invalidates an older sidecar)
    ///     over <paramref name="salt" /> before the platform KDF call: HKDF's own construction is
    ///     already HMAC-based, so folding the bank-id into <c>info</c> alongside the domain binds the
    ///     tag to (key material, domain, bank-id) in one audited primitive on top — no raw
    ///     <c>HMACSHA256</c> call needed (the no-hand-rolled-crypto gate reserves that shape for
    ///     applying a key already produced by a separate HKDF step, e.g. <c>SyncBlobAuthenticator</c>).
    ///     <paramref name="iterations" /> is 0 for a raw SQLCipher key literal — 32 bytes already as
    ///     random as PBKDF2 output, so the stretch is skipped and HKDF runs on it directly.
    /// </summary>
    private static byte[] ComputeTag(byte[] bankId, byte[] salt, int iterations, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var keyMaterial = iterations > 0
            ? Rfc2898DeriveBytes.Pbkdf2(keyBytes, salt, iterations, HashAlgorithmName.SHA256, TagLength)
            : keyBytes;

        byte[] info = [.. DomainBytes, .. bankId];
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial, TagLength, info: info);
    }

    /// <summary>
    ///     True when <paramref name="key" /> is SQLCipher's raw-key literal (<c>x'...'</c>, already
    ///     32 high-entropy bytes) rather than a human-typed passphrase — the case the PBKDF2
    ///     pre-stretch above exists to slow down. Anything that is not valid hex in that shape is
    ///     treated as a passphrase, the safer default.
    /// </summary>
    private static bool IsRawKeyLiteral(string key)
    {
        if (key.Length < 4 || (key[0] != 'x' && key[0] != 'X') || key[1] != '\'' || key[^1] != '\'')
        {
            return false;
        }

        var hex = key.AsSpan(2, key.Length - 3);
        return !hex.IsEmpty && hex.Length % 2 == 0 && IsHex(hex);
    }

    private static bool IsHex(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
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
