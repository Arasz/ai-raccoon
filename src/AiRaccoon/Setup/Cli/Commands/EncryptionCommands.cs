using System.CommandLine;
using System.Security.Cryptography;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Extensions;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     encryption bitwarden/show/unset handlers: bws presence check, interactive id
///     collection with configurable defaults, per-run-only -t token, reachability validation, and
///     the rekey→sidecar→settings persist order (sidecar is the pre-open source of truth; settings mirror it).
/// </summary>
public sealed partial class EncryptionCommands
{
    private readonly ISqliteConnectionFactory _bankConnectionFactory;
    private readonly ICliSecretManager _bws;
    private readonly IEncryptionKeyProvider _env;
    private readonly IEncryptionSourceSidecar _sidecar;
    private readonly ILogger<EncryptionCommands> _logger;

    public EncryptionCommands(
        ISqliteConnectionFactory bank,
        ICliSecretManager bws,
        IEncryptionKeyProvider env,
        IEncryptionSourceSidecar sidecar,
        ILogger<EncryptionCommands> logger)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(bws);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(logger);
        _bankConnectionFactory = bank;
        _bws = bws;
        _env = env;
        _sidecar = sidecar;
        _logger = logger;
    }

    /// <summary>Env vars an operator can set to offer their own project/secret id as the interactive default, instead of the fallback placeholder.</summary>
    public const string ProjectIdEnvVar = "AIRACCOON_BITWARDEN_PROJECT_ID";

    public const string SecretIdEnvVar = "AIRACCOON_BITWARDEN_SECRET_ID";

    // Obviously fake — no default should identify a real vault entry (no hardcoded secrets).
    private const string FallbackProjectId = "00000000-0000-0000-0000-000000000000";
    private const string FallbackSecretId = "11111111-1111-1111-1111-111111111111";

    private const string RotationWarning =
        "ai-raccoon: warning: rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank — rotate the secret and rekey the bank together";

    private const string MismatchText =
        "ai-raccoon: encryption mismatch: the bank cannot be opened with the bitwarden key — if the secret was rotated, the bank must be rekeyed (run 'ai-raccoon encryption bitwarden')";

    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private static string DefaultProjectId => Environment.GetEnvironmentVariable(ProjectIdEnvVar) is { Length: > 0 } value ? value : FallbackProjectId;

    private static string DefaultSecretId => Environment.GetEnvironmentVariable(SecretIdEnvVar) is { Length: > 0 } value ? value : FallbackSecretId;

    public async Task<int> BitwardenAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams,
        CancellationToken ctx)
    {
        try
        {
            _bws.Run(["--version"], null, PresenceTimeout);
        }
        catch (BwsInvocationException ex)
        {
            Log.BwsInvocationFailed(_logger, ex);
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        var projectId = await PromptAsync(streams, $"project id [{DefaultProjectId}]", DefaultProjectId, ctx);
        var secretId = await PromptAsync(streams, $"secret id [{DefaultSecretId}]", DefaultSecretId, ctx);

        var token = parseResult.GetResult("-t") is not null ? parseResult.GetValue<string>("-t") : null;
        BwsResult fetched;
        try
        {
            fetched = _bws.Run(["secret", "get", secretId], token, FetchTimeout);
        }
        catch (BwsInvocationException ex)
        {
            Log.BwsInvocationFailed(_logger, ex);
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        if (fetched.ExitCode != 0)
        {
            var errorLine = fetched.FirstErrorLine;
            Log.BwsCommandFailed(_logger, fetched.ExitCode, errorLine);
            await streams.WriteErrorLineAsync($"ai-raccoon: bws failed (exit {fetched.ExitCode}): {errorLine}");
            return 1;
        }

        byte[] seed;
        try
        {
            seed = OpenSshPrivateKeyParser.ParseSeed(fetched.Stdout.Trim());
        }
        catch (EncryptionKeyException ex)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        var derived = DeriveAndZeroSeed(seed);

        await streams.WriteErrorLineAsync(RotationWarning);

        var bankPath = _bankConnectionFactory.BankPath;
        if (File.Exists(bankPath))
        {
            Exception? openError = null;
            try
            {
                await using (await _bankConnectionFactory.OpenBankAsync(ctx))
                {
                }
            }

            catch (Exception ex) when (ex is SqliteException or BankKeyMismatchException)
            {
                openError = ex;
            }

            if (openError is null)
            {
                Log.RekeyingBank(_logger, BitwardenEncryptionKeyProvider.EncryptionSource);
                await _bankConnectionFactory.RekeyBankAsync(derived, ctx);
                Log.BankRekeyed(_logger, BitwardenEncryptionKeyProvider.EncryptionSource);
            }
            else if (await IsCorrectKey(derived, ctx))
            {
            }
            else
            {
                var envPassphrase = _env.GetPassphrase(new EncryptionData(EnvEncryptionKeyProvider.EncryptionSource)).Value;
                if (File.Exists(EncryptionSourceSidecar.PathFor(bankPath)) && !string.IsNullOrEmpty(envPassphrase) &&
                    await IsCorrectKey(envPassphrase, ctx))
                {
                    _sidecar.Delete();
                    await streams.WriteErrorLineAsync("ai-raccoon: bank is env-keyed; source was not switched");
                    return 0;
                }

                await streams.WriteErrorLineAsync(MismatchText);
                return 1;
            }
        }

        _sidecar.Write(new EncryptionData(BitwardenEncryptionKeyProvider.EncryptionSource) { ProjectId = projectId, SecretId = secretId });
        await store.SetSettingAsync(EncryptionSettingsKeys.Source, BitwardenEncryptionKeyProvider.EncryptionSource, ctx);
        await store.SetSettingAsync(EncryptionSettingsKeys.ProjectId, projectId, ctx);
        await store.SetSettingAsync(EncryptionSettingsKeys.SecretId, secretId, ctx);

        await streams.WriteOutputLineAsync("encryption source set to bitwarden");
        return 0;
    }

    private async Task<bool> IsCorrectKey(string key, CancellationToken ctx) => (await _bankConnectionFactory.ProbeUsingEncryptionKey(key, ctx)).IsCorrectKey;

    /// <summary>
    ///     Rekeys a bank still encrypted under the pre-ADR-0012 derivation. Explicit rather than
    ///     automatic on open: a rekey needs exclusive access to the bank
    ///     (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 3).
    /// </summary>
    public async Task<int> MigrateAsync(StandardStreams streams, CancellationToken cancellationToken)
    {
        if (!File.Exists(_bankConnectionFactory.BankPath))
        {
            await streams.WriteOutputLineAsync("no bank to migrate");
            return 0;
        }

        bool rekeyed;
        try
        {
            Log.MigratingBank(_logger, _bankConnectionFactory.BankPath);
            rekeyed = await _bankConnectionFactory.MigrateLegacyKeyAsync(cancellationToken);
        }
        catch (BankKeyMismatchException ex)
        {
            Log.MigrationRefused(_logger, _bankConnectionFactory.BankPath, ex);
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        if (rekeyed)
        {
            Log.BankMigrated(_logger, _bankConnectionFactory.BankPath);
            await streams.WriteOutputLineAsync("bank rekeyed to the current key derivation");
            return 0;
        }

        await streams.WriteOutputLineAsync("bank is already on the current key derivation; nothing to do");
        return 0;
    }

    public async Task<int> ShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await using var probe = await _bankConnectionFactory.OpenBankAsync(cancellationToken);

        var sourceRow = await store.GetSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
        var encryptionData = _sidecar.Read();
        if (sourceRow == BitwardenEncryptionKeyProvider.EncryptionSource || encryptionData.Source == BitwardenEncryptionKeyProvider.EncryptionSource)
        {
            var projectId = await store.GetSettingAsync(EncryptionSettingsKeys.ProjectId, cancellationToken)
                            ?? encryptionData.ProjectId ?? "(unset)";
            var secretId = await store.GetSettingAsync(EncryptionSettingsKeys.SecretId, cancellationToken)
                           ?? encryptionData.SecretId ?? "(unset)";
            await streams.WriteOutputLineAsync("source: bitwarden");
            await streams.WriteOutputLineAsync($"projectId: {projectId}");
            await streams.WriteOutputLineAsync($"secretId: {secretId}");
            return 0;
        }

        await streams.WriteOutputLineAsync("source: env");
        return 0;
    }

    public async Task<int> UnsetAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var bankPath = _bankConnectionFactory.BankPath;
        var bankExists = File.Exists(bankPath);

        if (bankExists)
        {
            await using (await _bankConnectionFactory.OpenBankAsync(cancellationToken))
            {
            }

            var envPassphrase = _env.GetPassphrase(new EncryptionData(EnvEncryptionKeyProvider.EncryptionSource)).Value;
            if (!string.IsNullOrEmpty(envPassphrase))
            {
                await store.DeleteSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
                await store.DeleteSettingAsync(EncryptionSettingsKeys.ProjectId, cancellationToken);
                await store.DeleteSettingAsync(EncryptionSettingsKeys.SecretId, cancellationToken);

                Log.RekeyingBank(_logger, EnvEncryptionKeyProvider.EncryptionSource);
                await _bankConnectionFactory.RekeyBankAsync(envPassphrase, cancellationToken);
                Log.BankRekeyed(_logger, EnvEncryptionKeyProvider.EncryptionSource);
                _sidecar.Delete();
            }
            else
            {
                Log.UnsetSkippedRekey(_logger);
                await streams.WriteErrorLineAsync(
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

        await streams.WriteOutputLineAsync("encryption source reset to env");
        return 0;
    }

    private static async Task<string> PromptAsync(StandardStreams streams, string label, string fallback,
        CancellationToken cancellationToken)
    {
        await streams.WriteErrorAsync($"{label}: ");
        var value = (await streams.ReadLineAsync(cancellationToken))?.Trim();
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
