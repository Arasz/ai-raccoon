using System.CommandLine;
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
///     encryption bitwarden/1password/show/unset handlers: CLI presence check, interactive id or
///     reference collection with configurable defaults, per-run-only -t token (bitwarden only),
///     reachability validation, and the rekey→sidecar→settings persist order (sidecar is the
///     pre-open source of truth; settings mirror it).
/// </summary>
public sealed partial class EncryptionCommands
{
    private readonly ISqliteConnectionFactory _bankConnectionFactory;
    private readonly ICliSecretManager _bws;
    private readonly ICliSecretManager _op;
    private readonly IEncryptionKeyProvider _env;
    private readonly IEncryptionSourceSidecar _sidecar;
    private readonly ILogger<EncryptionCommands> _logger;

    public EncryptionCommands(
        ISqliteConnectionFactory bank,
        ICliSecretManager bws,
        ICliSecretManager op,
        IEncryptionKeyProvider env,
        IEncryptionSourceSidecar sidecar,
        ILogger<EncryptionCommands> logger)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(bws);
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(logger);
        _bankConnectionFactory = bank;
        _bws = bws;
        _op = op;
        _env = env;
        _sidecar = sidecar;
        _logger = logger;
    }

    /// <summary>Env vars an operator can set to offer their own project/secret id as the interactive default, instead of the fallback placeholder.</summary>
    public const string ProjectIdEnvVar = "AIRACCOON_BITWARDEN_PROJECT_ID";

    public const string SecretIdEnvVar = "AIRACCOON_BITWARDEN_SECRET_ID";

    /// <summary>Env var an operator can set to offer their own 1Password secret reference as the interactive default.</summary>
    public const string OnePasswordReferenceEnvVar = "AIRACCOON_1PASSWORD_REFERENCE";

    // Obviously fake — no default should identify a real vault entry (no hardcoded secrets).
    private const string FallbackProjectId = "00000000-0000-0000-0000-000000000000";
    private const string FallbackSecretId = "11111111-1111-1111-1111-111111111111";
    private const string FallbackReference = "op://example-vault/example-item/private key?ssh-format=openssh";
    private const string ReferencePrefix = "op://";

    private const string NotAKeyHint =
        "use an SSH Key item and add ?ssh-format=openssh to the reference so op returns the OpenSSH private key";

    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BitwardenFetchTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OnePasswordFetchTimeout = TimeSpan.FromSeconds(30);

    private static string RotationWarning(string source) =>
        source == OnePasswordEncryptionKeyProvider.EncryptionSource
            ? "ai-raccoon: warning: rotating the key in 1Password without PRAGMA rekey bricks the bank — rotate the key and rekey the bank together"
            : "ai-raccoon: warning: rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank — rotate the secret and rekey the bank together";

    private static string MismatchText(string source) =>
        $"ai-raccoon: encryption mismatch: the bank cannot be opened with the {source} key — if the secret was rotated, the bank must be rekeyed (run 'ai-raccoon encryption {source}')";

    private static string DefaultProjectId => Environment.GetEnvironmentVariable(ProjectIdEnvVar) is { Length: > 0 } value ? value : FallbackProjectId;

    private static string DefaultReference => Environment.GetEnvironmentVariable(OnePasswordReferenceEnvVar) is { Length: > 0 } value ? value : FallbackReference;

    private static string DefaultSecretId => Environment.GetEnvironmentVariable(SecretIdEnvVar) is { Length: > 0 } value ? value : FallbackSecretId;

    public async Task<int> BitwardenAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams,
        CancellationToken ctx)
    {
        if (await CheckPresenceAsync(_bws, CliSecretTool.Bitwarden, streams, ctx) is { } missing)
        {
            return missing;
        }

        var projectId = await PromptAsync(streams, $"project id [{DefaultProjectId}]", DefaultProjectId, ctx);
        var secretId = await PromptAsync(streams, $"secret id [{DefaultSecretId}]", DefaultSecretId, ctx);

        var token = parseResult.GetResult("-t") is not null ? parseResult.GetValue<string>("-t") : null;
        var (fetched, failure) = await FetchAsync(_bws, CliSecretTool.Bitwarden, ["secret", "get", secretId], token, BitwardenFetchTimeout, streams, ctx);
        if (fetched is null)
        {
            return failure;
        }

        return await SwitchSourceAsync(CliSecretTool.Bitwarden, BitwardenEncryptionKeyProvider.EncryptionSource, fetched, null,
            new EncryptionData(BitwardenEncryptionKeyProvider.EncryptionSource) { ProjectId = projectId, SecretId = secretId }, store, streams, ctx);
    }

    public async Task<int> OnePasswordAsync(IMemoryStore store, StandardStreams streams, CancellationToken ctx)
    {
        if (await CheckPresenceAsync(_op, CliSecretTool.OnePassword, streams, ctx) is { } missing)
        {
            return missing;
        }

        var reference = await PromptAsync(streams, $"secret reference [{DefaultReference}]", DefaultReference, ctx);
        if (!reference.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: the secret reference must start with {ReferencePrefix} (for example {FallbackReference})");
            return ErrorCode.Usage.InvalidValue;
        }

        var (fetched, failure) = await FetchAsync(_op, CliSecretTool.OnePassword, ["read", reference], null, OnePasswordFetchTimeout, streams, ctx);
        if (fetched is null)
        {
            return failure;
        }

        return await SwitchSourceAsync(CliSecretTool.OnePassword, OnePasswordEncryptionKeyProvider.EncryptionSource, fetched, NotAKeyHint,
            new EncryptionData(OnePasswordEncryptionKeyProvider.EncryptionSource) { SecretId = reference }, store, streams, ctx);
    }

    /// <summary>Runs <c>--version</c>; returns the exit code when the CLI is missing or broken, null when it is usable.</summary>
    private async Task<int?> CheckPresenceAsync(ICliSecretManager runner, CliSecretTool tool, StandardStreams streams, CancellationToken ctx)
    {
        try
        {
            await runner.RunAsync(["--version"], null, PresenceTimeout, ctx);
            return null;
        }
        catch (CliSecretInvocationException ex)
        {
            Log.SecretCliInvocationFailed(_logger, tool.Name, ex);
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}");
            return CliFailureErrorCode.For(ex);
        }
    }

    /// <summary>Fetches the key text; a null result carries the exit code to return instead.</summary>
    private async Task<(CliSecretResult? Fetched, int Failure)> FetchAsync(ICliSecretManager runner, CliSecretTool tool, string[] args,
        string? token, TimeSpan timeout, StandardStreams streams, CancellationToken ctx)
    {
        CliSecretResult fetched;
        try
        {
            fetched = await runner.RunAsync(args, token, timeout, ctx);
        }
        catch (CliSecretInvocationException ex)
        {
            Log.SecretCliInvocationFailed(_logger, tool.Name, ex);
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}");
            return (null, CliFailureErrorCode.For(ex));
        }

        if (fetched.ExitCode != 0)
        {
            var errorLine = fetched.FirstErrorLine;
            Log.SecretCliCommandFailed(_logger, tool.Name, fetched.ExitCode, errorLine);
            await streams.WriteErrorLineAsync($"ai-raccoon: {tool.Name} failed (exit {fetched.ExitCode}): {errorLine}");
            return (null, ErrorCode.Key.SecretCliFailed);
        }

        return (fetched, 0);
    }

    /// <summary>
    ///     The shared tail of both source commands: derive the key, rekey the bank when it needs it, then
    ///     persist sidecar before settings. <paramref name="notAKeyHint" /> is appended to the not-a-key error.
    /// </summary>
    private async Task<int> SwitchSourceAsync(CliSecretTool tool, string source, CliSecretResult fetched, string? notAKeyHint,
        EncryptionData data, IMemoryStore store, StandardStreams streams, CancellationToken ctx)
    {
        string derived;
        try
        {
            derived = SshKeyPassphrase.FromOpenSsh(fetched.Stdout).Value;
        }
        catch (EncryptionKeyException ex)
        {
            await streams.WriteErrorLineAsync(notAKeyHint is null ? $"ai-raccoon: {ex.Message}" : $"ai-raccoon: {ex.Message} — {notAKeyHint}");
            return ErrorCode.Key.SecretNotAKey;
        }

        await streams.WriteErrorLineAsync(RotationWarning(source));

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

            catch (Exception ex) when (ex is SqliteException or BankKeyMismatchException or BankKeyMissingException)
            {
                openError = ex;
            }

            if (openError is null)
            {
                Log.RekeyingBank(_logger, source);
                await _bankConnectionFactory.RekeyBankAsync(derived, ctx);
                Log.BankRekeyed(_logger, source);
            }
            else if (await IsCorrectKey(derived, ctx))
            {
            }
            else
            {
                var envPassphrase = (await _env.GetPassphraseAsync(new EncryptionData(EnvEncryptionKeyProvider.EncryptionSource), ctx)).Value;
                if (File.Exists(EncryptionSourceSidecar.PathFor(bankPath)) && !string.IsNullOrEmpty(envPassphrase) &&
                    await IsCorrectKey(envPassphrase, ctx))
                {
                    _sidecar.Delete();
                    await streams.WriteErrorLineAsync("ai-raccoon: bank is env-keyed; source was not switched");
                    return ErrorCode.Key.BankKeyedToEnv;
                }

                await streams.WriteErrorLineAsync(MismatchText(source));
                return ErrorCode.Key.WrongKey;
            }
        }

        _sidecar.Write(data);
        await store.SetSettingAsync(EncryptionSettingsKeys.Source, source, ctx);
        if (data.ProjectId is null)
        {
            await store.DeleteSettingAsync(EncryptionSettingsKeys.ProjectId, ctx);
        }
        else
        {
            await store.SetSettingAsync(EncryptionSettingsKeys.ProjectId, data.ProjectId, ctx);
        }

        await store.SetSettingAsync(EncryptionSettingsKeys.SecretId, data.SecretId!, ctx);

        await streams.WriteOutputLineAsync($"encryption source set to {source}");
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
            return CliFailureErrorCode.For(ex);
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

        // The sidecar decides how the bank opens; the settings rows only mirror it and can be stale after an interrupted switch.
        var encryptionData = _sidecar.Read();
        var fromSidecar = encryptionData.Source != EncryptionData.NoneEncryptedSource;
        var source = fromSidecar ? encryptionData.Source : await store.GetSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
        var secretId = fromSidecar ? encryptionData.SecretId : await store.GetSettingAsync(EncryptionSettingsKeys.SecretId, cancellationToken);
        if (source == BitwardenEncryptionKeyProvider.EncryptionSource)
        {
            var projectId = fromSidecar ? encryptionData.ProjectId : await store.GetSettingAsync(EncryptionSettingsKeys.ProjectId, cancellationToken);
            await streams.WriteOutputLineAsync("source: bitwarden");
            await streams.WriteOutputLineAsync($"projectId: {projectId ?? "(unset)"}");
            await streams.WriteOutputLineAsync($"secretId: {secretId ?? "(unset)"}");
            return 0;
        }

        if (source == OnePasswordEncryptionKeyProvider.EncryptionSource)
        {
            await streams.WriteOutputLineAsync("source: 1password");
            await streams.WriteOutputLineAsync($"reference: {secretId ?? "(unset)"}");
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

            var envPassphrase = (await _env.GetPassphraseAsync(new EncryptionData(EnvEncryptionKeyProvider.EncryptionSource), cancellationToken)).Value;
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
                var currentSource = _sidecar.Read().Source;
                Log.UnsetSkippedRekey(_logger, currentSource);
                await streams.WriteErrorLineAsync(
                    $"ai-raccoon: warning: no AIRACCOON_DB_PASSPHRASE set — the bank stays keyed to the {currentSource} secret; set AIRACCOON_DB_PASSPHRASE and re-run 'ai-raccoon encryption unset' to rekey it back to the env passphrase (automatic decryption without an env passphrase is not supported)");
                return ErrorCode.Key.NoEnvPassphrase;
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 800, Level = LogLevel.Information, Message = "Rekeying the bank to the {Source} encryption key")]
        public static partial void RekeyingBank(ILogger logger, string source);

        [LoggerMessage(EventId = 801, Level = LogLevel.Information, Message = "Bank rekeyed to the {Source} encryption key")]
        public static partial void BankRekeyed(ILogger logger, string source);

        // 802/803/804/807 log at Debug, not Error/Warning: each one fires alongside a manual
        // WriteErrorLineAsync carrying the same message on the CLI's own stream (UX-F8) -- at
        // Error/Warning, the default (non-quiet) console's AddConsole also prints them, so the
        // failure appears twice: once as the clean CLI line, once as "fail: ...EncryptionCommands[NNN] ...".
        // --quiet mode is unaffected (QuietLogging sets its minimum level to Trace).
        [LoggerMessage(EventId = 802, Level = LogLevel.Debug, Message = "{Tool} invocation failed")]
        public static partial void SecretCliInvocationFailed(ILogger logger, string tool, Exception exception);

        [LoggerMessage(EventId = 803, Level = LogLevel.Debug, Message = "Bank stays keyed to the {Source} secret: AIRACCOON_DB_PASSPHRASE is not set")]
        public static partial void UnsetSkippedRekey(ILogger logger, string source);

        [LoggerMessage(EventId = 804, Level = LogLevel.Debug, Message = "{Tool} failed (exit {ExitCode}): {Error}")]
        public static partial void SecretCliCommandFailed(ILogger logger, string tool, int exitCode, string error);

        // The migration records nothing in the bank or the sidecar
        // (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 4) — these events are the whole audit trail.
        [LoggerMessage(EventId = 805, Level = LogLevel.Information, Message = "Checking the bank at {BankPath} for the pre-ADR-0012 key derivation")]
        public static partial void MigratingBank(ILogger logger, string bankPath);

        [LoggerMessage(EventId = 806, Level = LogLevel.Information, Message = "Bank at {BankPath} rekeyed to the current key derivation")]
        public static partial void BankMigrated(ILogger logger, string bankPath);

        [LoggerMessage(EventId = 807, Level = LogLevel.Debug, Message = "Refused to rekey the bank at {BankPath}: it was left unmodified")]
        public static partial void MigrationRefused(ILogger logger, string bankPath, Exception exception);
    }
}
