using System.CommandLine;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     encryption bitwarden/show/unset handlers: bws presence check, interactive id
///     collection with owner defaults, per-run-only -t token, reachability validation, and the
///     rekey→sidecar→settings persist order (sidecar is the pre-open source of truth; settings mirror it).
/// </summary>
public sealed partial class EncryptionCommands : IEncryptionCommands
{
    private const string DefaultProjectId = "613165e6-7947-49e0-889b-b49d007c5b85";
    private const string DefaultSecretId = "f1d3c8e5-5391-4aef-8611-b49d007c8702";

    private const string RotationWarning =
        "ai-raccoon: warning: rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank — rotate the secret and rekey the bank together";

    private const string MismatchText =
        "ai-raccoon: encryption mismatch: the bank cannot be opened with the bitwarden key — if the secret was rotated, the bank must be rekeyed (run 'ai-raccoon encryption bitwarden')";

    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private readonly SqliteConnectionFactory _bank;
    private readonly ICliSecretManager _bws;
    private readonly IEncryptionKeyProvider _env;
    private readonly IEncryptionState _encryptionState;
    private readonly ILogger _logger;

    public EncryptionCommands(SqliteConnectionFactory bank, ICliSecretManager bws,
        IEncryptionKeyProvider env, IEncryptionState encryptionState, ILogger logger)
    {
        _bank = bank;
        _bws = bws;
        _env = env;
        _encryptionState = encryptionState;
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

        var derived = SshKeyDerivation.DeriveRawKey(seed);

        await stderr.WriteLineAsync(RotationWarning);

        var bankPath = _bank.BankPath;
        if (File.Exists(bankPath))
        {
            SqliteException? openError = null;
            try
            {
                await using (var probe = await _bank.OpenBankAsync(cancellationToken))
                {
                }
            }
            catch (SqliteException ex)
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
                if (File.Exists(EncryptionState.PathFor(bankPath)) && !string.IsNullOrEmpty(envPassphrase) &&
                    await TryOpenAsync(_bank, envPassphrase, cancellationToken))
                {
                    _encryptionState.Delete();
                    await stderr.WriteLineAsync("ai-raccoon: bank is env-keyed; source was not switched");
                    return 0;
                }

                await stderr.WriteLineAsync(MismatchText);
                return 1;
            }
        }

        _encryptionState.Write(new EncryptionData(BitwardenEncryptionKeyProvider.EncryptionSource) { ProjectId = projectId, SecretId = secretId });
        await store.SetSettingAsync(EncryptionSettingsKeys.Source, BitwardenEncryptionKeyProvider.EncryptionSource, cancellationToken);
        await store.SetSettingAsync(EncryptionSettingsKeys.ProjectId, projectId, cancellationToken);
        await store.SetSettingAsync(EncryptionSettingsKeys.SecretId, secretId, cancellationToken);

        await stdout.WriteLineAsync("encryption source set to bitwarden");
        return 0;
    }

    public async Task<int> ShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        await using var probe = await _bank.OpenBankAsync(cancellationToken);

        var sourceRow = await store.GetSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
        var sidecar = _encryptionState.Read();
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
            await using (var probe = await _bank.OpenBankAsync(cancellationToken))
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
                _encryptionState.Delete();
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
            _encryptionState.Delete();
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
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Rekeying the bank to the {Source} encryption key")]
        public static partial void RekeyingBank(ILogger logger, string source);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Bank rekeyed to the {Source} encryption key")]
        public static partial void BankRekeyed(ILogger logger, string source);

        [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "bws invocation failed")]
        public static partial void BwsInvocationFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Bank stays keyed to the bitwarden secret: AIRACCOON_DB_PASSPHRASE is not set")]
        public static partial void UnsetSkippedRekey(ILogger logger);

        [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "bws failed (exit {ExitCode}): {Error}")]
        public static partial void BwsCommandFailed(ILogger logger, int exitCode, string error);
    }
}
