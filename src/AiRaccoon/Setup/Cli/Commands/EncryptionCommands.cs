using System.CommandLine;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using CommunityToolkit.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     encryption bitwarden/show/unset handlers (plan §S3): bws presence check, interactive id
///     collection with owner defaults, per-run-only -t token, reachability validation, and the
///     rekey→sidecar→settings persist order (sidecar is the pre-open source of truth; settings mirror it).
/// </summary>
internal static partial class ConfigCommands
{
    private const string DefaultProjectId = "613165e6-7947-49e0-889b-b49d007c5b85";
    private const string DefaultSecretId = "f1d3c8e5-5391-4aef-8611-b49d007c8702";

    private const string RotationWarning =
        "ai-raccoon: warning: rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank — rotate the secret and rekey the bank together";

    private const string MismatchText =
        "ai-raccoon: encryption mismatch: the bank cannot be opened with the bitwarden key — if the secret was rotated, the bank must be rekeyed (run 'ai-raccoon encryption bitwarden')";

    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private static async Task<int> EncryptionBitwardenAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin, SqliteConnectionFactory? bank,
        ICliSecretManager? bws, IEncryptionKeyProvider? env, IEncryptionState? encryptionState, ILogger? logger,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNull(bank);
        Guard.IsNotNull(bws);
        Guard.IsNotNull(env);
        Guard.IsNotNull(encryptionState);
        Guard.IsNotNull(logger);

        try
        {
            bws.Run(["--version"], null, PresenceTimeout);
        }
        catch (BwsInvocationException ex)
        {
            Log.BwsInvocationFailed(logger, ex);
            await stderr.WriteLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        // (b) Interactive collection; empty input = the owner default (ids are not secrets).
        var projectId = await PromptAsync(stderr, stdin, $"project id [{DefaultProjectId}]", DefaultProjectId, cancellationToken);
        var secretId = await PromptAsync(stderr, stdin, $"secret id [{DefaultSecretId}]", DefaultSecretId, cancellationToken);

        // (c) Reachability validation: fetch + parse + derive, with the per-run token only.
        // Any failure = §5.4 error, exit 1, no state change.
        var token = parseResult.GetResult("-t") is not null ? parseResult.GetValue<string>("-t") : null;
        BwsResult fetched;
        try
        {
            fetched = bws.Run(["secret", "get", secretId], token, FetchTimeout);
        }
        catch (BwsInvocationException ex)
        {
            Log.BwsInvocationFailed(logger, ex);
            await stderr.WriteLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }

        if (fetched.ExitCode != 0)
        {
            var errorLine = FirstStderrLine(fetched.Stderr);
            Log.BwsCommandFailed(logger, fetched.ExitCode, errorLine);
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

        // (d) Rotation warning (config success path).
        await stderr.WriteLineAsync(RotationWarning);

        // (e) Bank: rekey the existing bank to the derived key, self-healing the crash
        // windows. Fresh bank (no file yet): skip rekey — the first server start creates it.
        var bankPath = bank.BankPath;
        if (File.Exists(bankPath))
        {
            SqliteException? openError = null;
            try
            {
                await using (var probe = await bank.OpenBankAsync(cancellationToken))
                {
                }
            }
            catch (SqliteException ex)
            {
                openError = ex;
            }

            if (openError is null)
            {
                // Current-source open succeeded → rekey to the derived key (verify-reopen inside).
                Log.RekeyingBank(logger, BitwardenEncryptionKeyProvider.EncryptionSource);
                await bank.RekeyBankAsync(derived, cancellationToken);
                Log.BankRekeyed(logger, BitwardenEncryptionKeyProvider.EncryptionSource);
            }
            else if (await TryOpenAsync(bank, derived, cancellationToken))
            {
                // Crash window between rekey and sidecar write: the bank is already
                // derived-keyed — skip the rekey and complete the persistence.
            }
            else
            {
                // Amendment 1 (unset crash window): rekey-back landed but the sidecar was never
                // deleted. The env key opens the bank → report and leave the sidecar consistent.
                var envPassphrase = env.GetPassphrase(new EncryptionData(EnvEncryptionKeyProvider.EncryptionSource)).Value;
                if (File.Exists(EncryptionState.PathFor(bankPath)) && !string.IsNullOrEmpty(envPassphrase) &&
                    await TryOpenAsync(bank, envPassphrase, cancellationToken))
                {
                    encryptionState.Delete();
                    await stderr.WriteLineAsync("ai-raccoon: bank is env-keyed; source was not switched");
                    return 0;
                }

                await stderr.WriteLineAsync(MismatchText);
                return 1;
            }
        }

        // (f) Persist: sidecar FIRST (the resolver reads it pre-open), then the settings
        // mirror — the store's opens now resolve the bitwarden key because the sidecar is set.
        encryptionState.Write(new EncryptionData(BitwardenEncryptionKeyProvider.EncryptionSource) { ProjectId = projectId, SecretId = secretId });
        await store.SetSettingAsync(EncryptionSettingsKeys.Source, BitwardenEncryptionKeyProvider.EncryptionSource, cancellationToken);
        await store.SetSettingAsync(EncryptionSettingsKeys.ProjectId, projectId, cancellationToken);
        await store.SetSettingAsync(EncryptionSettingsKeys.SecretId, secretId, cancellationToken);

        await stdout.WriteLineAsync("encryption source set to bitwarden");
        return 0;
    }

    private static async Task<int> EncryptionShowAsync(IMemoryStore store, TextWriter stdout,
        SqliteConnectionFactory? bank, IEncryptionState? encryptionState, ILogger? logger,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNull(encryptionState);
        Guard.IsNotNull(logger);

        if (bank is not null)
        {
            // Validate the bank opens with the configured source (loud on corrupt sidecar).
            await using var probe = await bank.OpenBankAsync(cancellationToken);
        }

        var sourceRow = await store.GetSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
        var sidecar = bank is not null ? encryptionState.Read() : null;
        if (sourceRow == BitwardenEncryptionKeyProvider.EncryptionSource || sidecar?.Source == BitwardenEncryptionKeyProvider.EncryptionSource)
        {
            // Settings first, sidecar fallback (crash-window self-description, plan §4).
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

    private static async Task<int> EncryptionUnsetAsync(IMemoryStore store, TextWriter stdout, TextWriter stderr,
        SqliteConnectionFactory? bank, IEncryptionKeyProvider? env, IEncryptionState? encryptionState, ILogger? logger,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNull(bank);
        Guard.IsNotNull(env);
        Guard.IsNotNull(encryptionState);
        Guard.IsNotNull(logger);
        var bankPath = bank.BankPath;
        var bankExists = File.Exists(bankPath);

        if (bankExists)
        {
            // Open with the current source (sidecar still says bitwarden) to prove the key works.
            await using (var probe = await bank.OpenBankAsync(cancellationToken))
            {
            }

            var envPassphrase = env.GetPassphrase(new EncryptionData("env")).Value;
            if (!string.IsNullOrEmpty(envPassphrase))
            {
                // Rows first — the store still opens with the bitwarden key at this point.
                await store.DeleteSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
                await store.DeleteSettingAsync(EncryptionSettingsKeys.ProjectId, cancellationToken);
                await store.DeleteSettingAsync(EncryptionSettingsKeys.SecretId, cancellationToken);

                // Rekey back to the env passphrase (current key resolves from the sidecar).
                Log.RekeyingBank(logger, EnvEncryptionKeyProvider.EncryptionSource);
                await bank.RekeyBankAsync(envPassphrase, cancellationToken);
                Log.BankRekeyed(logger, EnvEncryptionKeyProvider.EncryptionSource);
                encryptionState.Delete();
            }
            else
            {
                // No env passphrase: DO NOT auto-decrypt (deferred empty-rekey per plan §7.3) —
                // warn loudly and LEAVE the sidecar + rows in place (source stays bitwarden) so
                // the documented retry actually works: setting AIRACCOON_DB_PASSPHRASE and
                // re-running unset reopens with the bitwarden key and rekeys back.
                Log.UnsetSkippedRekey(logger);
                await stderr.WriteLineAsync(
                    "ai-raccoon: warning: no AIRACCOON_DB_PASSPHRASE set — the bank stays keyed to the bitwarden secret; set AIRACCOON_DB_PASSPHRASE and re-run 'ai-raccoon encryption unset' to rekey it back to the env passphrase (automatic decryption without an env passphrase is not supported)");
                return 1;
            }
        }
        else
        {
            // No bank exists — nothing is stranded; clean up the mirror rows and the sidecar.
            await store.DeleteSettingAsync(EncryptionSettingsKeys.Source, cancellationToken);
            await store.DeleteSettingAsync(EncryptionSettingsKeys.ProjectId, cancellationToken);
            await store.DeleteSettingAsync(EncryptionSettingsKeys.SecretId, cancellationToken);
            encryptionState.Delete();
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

    private static string FirstStderrLine(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? "(no stderr)";
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
