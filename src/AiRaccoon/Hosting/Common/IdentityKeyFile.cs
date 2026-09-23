using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     The per-root ECDSA P-256 trust anchor, kept as a 0600 PKCS#8 PEM in the bank state directory
///     (ADR-0106 D1). Only `serve` mints or heals it; a verifier reads it and creates nothing — a
///     missing key is "cannot attach", never "mint one here".
/// </summary>
public sealed class IdentityKeyFile
{
    public const string FileName = "identity-key";

    /// <summary>How long debris is given to fill in before it counts as a crash's leftovers.</summary>
    public static readonly TimeSpan HealAfter = TimeSpan.FromSeconds(5);

    private const int KeySize = 256;

    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Serializes mint and heal across callers in this process; the lock file does so across processes.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TimeSpan _healAfter;
    private readonly TimeProvider _timeProvider;
    private ECDsa? _signer;

    public IdentityKeyFile(InfrastructureOptions options, TimeProvider? timeProvider = null, TimeSpan? healAfter = null)
    {
        Guard.IsNotNull(options);
        StateDirectory = BankPaths.DirectoryFor(options);
        Path = System.IO.Path.Combine(StateDirectory, FileName);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healAfter = healAfter ?? HealAfter;
        Guard.IsGreaterThan(_healAfter, TimeSpan.Zero);
    }

    /// <summary>Directory holding the key: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon for project scope.</summary>
    public string StateDirectory { get; }

    /// <summary>Absolute path of the key file — the one thing an operator has to look at.</summary>
    public string Path { get; }

    /// <summary>Why the last ensure or read refused, with the remedy; null when it did not.</summary>
    public string? RefusalReason { get; private set; }

    /// <summary>True when the last ensure refused because the state directory or a secret file is not owner-only.</summary>
    public bool NotOwnerOnly { get; private set; }

    /// <summary>True when the last ensure tightened an owned state directory others could only read to 0700.</summary>
    public bool TightenedStateDirectory { get; private set; }

    /// <summary>
    ///     The key, minted or healed when needed; null when the state directory or an existing file is
    ///     not owner-only, or the file cannot be written. Only `serve` calls this.
    /// </summary>
    public async Task<ECDsa?> EnsureAsync(CancellationToken cancellationToken)
    {
        RefusalReason = null;
        NotOwnerOnly = false;
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
    ///     The cached signer, or null when the file is missing, unreadable, not P-256, or shared.
    ///     Read-only by construction: no directory, lock file or key file is ever created here.
    /// </summary>
    public ECDsa? Read()
    {
        RefusalReason = null;
        if (!OwnerOnlyFile.IsDirectoryPrivate(StateDirectory))
        {
            RefusalReason =
                $"the state directory '{StateDirectory}' is not owner-only — run 'chmod 700 \"{StateDirectory}\"' and start again";
            return null;
        }

        return ReadStateKey();
    }

    /// <summary>The keyId the stored key would sign as; null exactly when <see cref="Read" /> is null.</summary>
    public string? ReadKeyId() => Read() is { } key ? IdentityProof.KeyId(key) : null;

    /// <summary>The lock a concurrent mint/heal takes; exposed for the cross-process convergence gate.</summary>
    internal static string LockPathFor(string stateDirectory) =>
        OwnerOnlyFile.LockPathFor(System.IO.Path.Combine(stateDirectory, FileName));

    private async Task<ECDsa?> EnsureLockedAsync(CancellationToken cancellationToken)
    {
        if (ReadStateKey() is { } existing)
        {
            return existing;
        }

        if (await AcquireAsync(cancellationToken).ConfigureAwait(false) is { } minted)
        {
            return minted;
        }

        if (!TryDeleteDebris())
        {
            // A live writer may have finished while the heal wait ran; only the parse decides.
            return ReadStateKey();
        }

        return await AcquireAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads or mints, retrying until the heal wait expires.</summary>
    private async Task<ECDsa?> AcquireAsync(CancellationToken cancellationToken)
    {
        using var waited = new CancellationTokenSource(_healAfter, _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, waited.Token);
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        try
        {
            do
            {
                if (ReadStateKey() is { } existing)
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
            // The wait expired, not the caller: report the miss rather than throwing.
        }

        return null;
    }

    /// <summary>A newly minted signer, or null when the file already exists or cannot be created.</summary>
    internal async Task<ECDsa?> TryMintAsync(CancellationToken cancellationToken)
    {
        var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = fresh.ExportPkcs8PrivateKeyPem();
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFile.OwnerFileMode;
        }

        try
        {
            await using var stream = new FileStream(Path, options);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(pem), cancellationToken).ConfigureAwait(false);
            _signer = fresh;
            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fresh.Dispose();
            return null;
        }
    }

    /// <summary>Deletes the file only while it holds no key and is older than the heal window.</summary>
    internal bool TryDeleteDebris()
    {
        try
        {
            using (var held = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using var reader = new StreamReader(held, Encoding.UTF8);
                if (ParseKey(reader.ReadToEnd(), out _) is { } probe)
                {
                    probe.Dispose();
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

    private ECDsa? ReadStateKey()
    {
        if (_signer is not null)
        {
            return _signer;
        }

        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            OwnerOnlyFile.EnsureFileIsPrivate(Path);
            _signer = ParseKey(File.ReadAllText(Path), out var refusal);
            RefusalReason = refusal;
            return _signer;
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

    /// <summary>The key in <paramref name="pem" />, or null with a named refusal when it is not a usable P-256 private key.</summary>
    private ECDsa? ParseKey(string pem, out string? refusal)
    {
        refusal = null;
        try
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(pem);
                var parameters = key.ExportParameters(false);
                if (key.KeySize != KeySize || !string.Equals(parameters.Curve.Oid.Value, NistP256Oid, StringComparison.Ordinal))
                {
                    refusal = $"'{Path}' is not an ECDSA P-256 identity key — remove it and start serve again to mint one";
                    key.Dispose();
                    return null;
                }

                // A public-only PEM imports fine; prove a private key exists before trusting it.
                _ = key.ExportPkcs8PrivateKey();
            }
            catch
            {
                key.Dispose();
                throw;
            }

            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
