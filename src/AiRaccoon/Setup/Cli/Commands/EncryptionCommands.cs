using System.CommandLine;
using System.Security.Cryptography;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using CommunityToolkit.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     encryption bitwarden/show/unset handlers: bws presence check, interactive id
///     collection with configurable defaults, per-run-only -t token, reachability validation, and
///     the rekey→sidecar→settings persist order (sidecar is the pre-open source of truth; settings mirror it).
/// </summary>
public sealed partial class EncryptionCommands
{
    /// <summary>Env vars an operator can set to offer their own project/secret id as the interactive default, instead of the fallback placeholder.</summary>
    public const string ProjectIdEnvVar = "AIRACCOON_BITWARDEN_PROJECT_ID";

    public const string SecretIdEnvVar = "AIRACCOON_BITWARDEN_SECRET_ID";

    // Obviously fake — no default should identify a real vault entry (no hardcoded secrets).
    private const string FallbackProjectId = "00000000-0000-0000-0000-000000000000";
    private const string FallbackSecretId = "11111111-1111-1111-1111-111111111111";

    private static string DefaultProjectId => Environment.GetEnvironmentVariable(ProjectIdEnvVar) is { Length: > 0 } value ? value : FallbackProjectId;

    private static string DefaultSecretId => Environment.GetEnvironmentVariable(SecretIdEnvVar) is { Length: > 0 } value ? value : FallbackSecretId;

    private const string RotationWarning =
        "ai-raccoon: warning: rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank — rotate the secret and rekey the bank together";

    private const string MismatchText =
        "ai-raccoon: encryption mismatch: the bank cannot be opened with the bitwarden key — if the secret was rotated, the bank must be rekeyed (run 'ai-raccoon encryption bitwarden')";

    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private readonly SqliteConnectionFactory _bank;
    private readonly ICliSecretManager _bws;
    private readonly IEncryptionKeyProvider _env;
    private readonly ILogger _logger;
    private readonly IEncryptionSourceSidecar _sidecar;

    public EncryptionCommands(SqliteConnectionFactory bank, ICliSecretManager bws,
        IEncryptionKeyProvider env, IEncryptionSourceSidecar sidecar, ILogger logger)
    {
        Guard.IsNotNull(bank);
        Guard.IsNotNull(bws);
        Guard.IsNotNull(env);
        Guard.IsNotNull(sidecar);
        Guard.IsNotNull(logger);

        _bank = bank;
        _bws = bws;
        _env = env;
        _sidecar = sidecar;
        _logger = logger;
    }

    public async Task<int> BitwardenAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
        CancellationToken cancellationToken)
    {
        try
        {
            _bws.Run(["--version"], null, PresenceTimeout);
        }
        catch (BwsInvocationException ex)
        {
            Log.BwsInvocationFailed(_logger, ex);
            await stderr.WriteLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        var projectId = await PromptAsync(stderr, stdin, $"project id [{DefaultProjectId}]", DefaultProjectId, cancellationToken);
        var secretId = await PromptAsync(stderr, stdin, $"secret id [{DefaultSecretId}]", DefaultSecretId, cancellationToken);

        var token = parseResult.GetResult("-t") is not null ? parseResult.GetValue<string>("-t") : null;
        BwsResult fetched;
        try
        {
            fetched = _bws.Run(["secret", "get", secretId], token, FetchTimeout);
        }
        catch (BwsInvocationException ex)
        {
            Log.BwsInvocationFailed(_logger, ex);
            await stderr.WriteLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        if (fetched.ExitCode != 0)
        {
            var errorLine = fetched.FirstErrorLine;
            Log.BwsCommandFailed(_logger, fetched.ExitCode, errorLine);
            await stderr.WriteLineAsync($"ai-raccoon: bws failed (exit {fetched.ExitCode}): {errorLine}");
            return 1;
        }

        byte[] seed;
        try
        {
            seed = OpenSshPrivateKeyParser.ParseSeed(fetched.Stdout.Trim());
        }
        catch (EncryptionKeyException ex)
        {
            await stderr.WriteLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        var derived = DeriveAndZeroSeed(seed);

        await stderr.WriteLineAsync(RotationWarning);

        var bankPath = _bank.BankPath;
        if (File.Exists(bankPath))
        {
            Exception? openError = null;
            try
            {
                using (await _bank.OpenBankAsync(cancellationToken))
                {
                }
            }
            // Post-ADR-0012 a bank that fails to open under the resolved key surfaces as
            // BankKeyMismatchException, not a bare SqliteException; both mean "did not open" here.
            catch (Exception ex) when (ex is SqliteException or BankKeyMismatchException)
            {
                openError = ex;
            }

            if (openError is null)
            {
                Log.RekeyingBank(_logger, BitwardenEncryptionKeyProvider.EncryptionSource);
                await _bank.RekeyBankAsync(derived, cancellationToken);
                Log.BankRekeyed(_logger, BitwardenEncryptionKeyProvider.EncryptionSource);
            }
            else if (await TryOpenAsync(_bank, derived, cancellationToken))
            {
            }
            else
            {
                var envPassphrase = _env.GetPassphrase(new EncryptionData(EnvEncryptionKeyProvider.EncryptionSource)).Value;
                if (File.Exists(EncryptionSourceSidecar.PathFor(bankPath)) && !string.IsNullOrEmpty(envPassphrase) &&
                    await TryOpenAsync(_bank, envPassphrase, cancellationToken))
                {
                    _sidecar.Delete();
                    await stderr.WriteLineAsync("ai-raccoon: bank is env-keyed; source was not switched");
                    return 0;
                }

                await stderr.WriteLineAsync(MismatchText);
                return 1;
            }
        }

        _sidecar.Write(new EncryptionData(BitwardenEncryptionKeyProvider.EncryptionSource) { ProjectId = projectId, SecretId = secretId });
        await store.SetSettingAsync(EncryptionSettingsKeys.Source, BitwardenEncryptionKeyProvider.EncryptionSource, cancellationToken);
        await store.SetSettingAsync(EncryptionSettingsKeys.ProjectId, projectId, cancellationToken);
        await store.SetSettingAsync(EncryptionSettingsKeys.SecretId, secretId, cancellationToken);

        await stdout.WriteLineAsync("encryption source set to bitwarden");
        return 0;
    }

    /// <summary>
    ///     Rekeys a bank still encrypted under the pre-ADR-0012 derivation. Explicit rather than
    ///     automatic on open: a rekey needs exclusive access to the bank
    ///     (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 3).
    /// </summary>
    public async Task<int> MigrateAsync(TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (!File.Exists(_bank.BankPath))
        {
            await stdout.WriteLineAsync("no bank to migrate");
            return 0;
        }

        bool rekeyed;
        try
        {
            Log.MigratingBank(_logger, _bank.BankPath);
            rekeyed = await _bank.MigrateLegacyKeyAsync(cancellationToken);
        }
        catch (BankKeyMismatchException ex)
        {
            Log.MigrationRefused(_logger, _bank.BankPath, ex);
            await stderr.WriteLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        if (rekeyed)
        {
            Log.BankMigrated(_logger, _bank.BankPath);
            await stdout.WriteLineAsync("bank rekeyed to the current key derivation");
            return 0;
        }

        await stdout.WriteLineAsync("bank is already on the current key derivation; nothing to do");
        return 0;
    }

    public async Task<int> ShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        await using var probe = await _bank.OpenBankAsync(cancellationToken);

        var sourceRow = await store.GetSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
        var sidecar = _sidecar.Read();
        if (sourceRow == BitwardenEncryptionKeyProvider.EncryptionSource || sidecar?.Source == BitwardenEncryptionKeyProvider.EncryptionSource)
        {
            var projectId = await store.GetSettingAsync(EncryptionSettingsKeys.ProjectId, cancellationToken)
                            ?? sidecar?.ProjectId ?? "(unset)";
            var secretId = await store.GetSettingAsync(EncryptionSettingsKeys.SecretId, cancellationToken)
                           ?? sidecar?.SecretId ?? "(unset)";
            await stdout.WriteLineAsync("source: bitwarden");
            await stdout.WriteLineAsync($"projectId: {projectId}");
            await stdout.WriteLineAsync($"secretId: {secretId}");
            return 0;
        }

        await stdout.WriteLineAsync("source: env");
        return 0;
    }

    public async Task<int> UnsetAsync(IMemoryStore store, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var bankPath = _bank.BankPath;
        var bankExists = File.Exists(bankPath);

        if (bankExists)
        {
            using (await _bank.OpenBankAsync(cancellationToken))
            {
            }

            var envPassphrase = _env.GetPassphrase(new EncryptionData(EnvEncryptionKeyProvider.EncryptionSource)).Value;
            if (!string.IsNullOrEmpty(envPassphrase))
            {
                await store.DeleteSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
                await store.DeleteSettingAsync(EncryptionSettingsKeys.ProjectId, cancellationToken);
                await store.DeleteSettingAsync(EncryptionSettingsKeys.SecretId, cancellationToken);

                Log.RekeyingBank(_logger, EnvEncryptionKeyProvider.EncryptionSource);
                await _bank.RekeyBankAsync(envPassphrase, cancellationToken);
                Log.BankRekeyed(_logger, EnvEncryptionKeyProvider.EncryptionSource);
                _sidecar.Delete();
            }
            else
            {
                Log.UnsetSkippedRekey(_logger);
                await stderr.WriteLineAsync(
                    "ai-raccoon: warning: no AIRACCOON_DB_PASSPHRASE set — the bank stays keyed to the bitwarden secret; set AIRACCOON_DB_PASSPHRASE and re-run 'ai-raccoon encryption unset' to rekey it back to the env passphrase (automatic decryption without an env passphrase is not supported)");
                return 1;
            }
        }
        else
        {
            await store.DeleteSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
            await store.DeleteSettingAsync(EncryptionSettingsKeys.ProjectId, cancellationToken);
            await store.DeleteSettingAsync(EncryptionSettingsKeys.SecretId, cancellationToken);
            _sidecar.Delete();
        }

        await stdout.WriteLineAsync("encryption source reset to env");
        return 0;
    }

    private static async Task<string> PromptAsync(TextWriter stderr, TextReader stdin, string label, string fallback,
        CancellationToken cancellationToken)
    {
        await stderr.WriteAsync($"{label}: ");
        var value = (await stdin.ReadLineAsync(cancellationToken))?.Trim();
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    /// <summary>Derives the raw key from the seed, then zeroes it — the seed must not outlive this call.</summary>
    internal static string DeriveAndZeroSeed(byte[] seed)
    {
        try
        {
            return SshKeyDerivation.DeriveRawKey(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static async Task<bool> TryOpenAsync(SqliteConnectionFactory bank, string key,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var probe = await bank.OpenBankWithKeyAsync(key, cancellationToken);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 800, Level = LogLevel.Information, Message = "Rekeying the bank to the {Source} encryption key")]
        public static partial void RekeyingBank(ILogger logger, string source);

        [LoggerMessage(EventId = 801, Level = LogLevel.Information, Message = "Bank rekeyed to the {Source} encryption key")]
        public static partial void BankRekeyed(ILogger logger, string source);

        [LoggerMessage(EventId = 802, Level = LogLevel.Error, Message = "bws invocation failed")]
        public static partial void BwsInvocationFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 803, Level = LogLevel.Warning, Message = "Bank stays keyed to the bitwarden secret: AIRACCOON_DB_PASSPHRASE is not set")]
        public static partial void UnsetSkippedRekey(ILogger logger);

        [LoggerMessage(EventId = 804, Level = LogLevel.Error, Message = "bws failed (exit {ExitCode}): {Error}")]
        public static partial void BwsCommandFailed(ILogger logger, int exitCode, string error);

        // The migration records nothing in the bank or the sidecar
        // (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 4) — these events are the whole audit trail.
        [LoggerMessage(EventId = 805, Level = LogLevel.Information, Message = "Checking the bank at {BankPath} for the pre-ADR-0012 key derivation")]
        public static partial void MigratingBank(ILogger logger, string bankPath);

        [LoggerMessage(EventId = 806, Level = LogLevel.Information, Message = "Bank at {BankPath} rekeyed to the current key derivation")]
        public static partial void BankMigrated(ILogger logger, string bankPath);

        [LoggerMessage(EventId = 807, Level = LogLevel.Error, Message = "Refused to rekey the bank at {BankPath}: it was left unmodified")]
        public static partial void MigrationRefused(ILogger logger, string bankPath, Exception exception);
    }
}
