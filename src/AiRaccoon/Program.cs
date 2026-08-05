using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Microsoft.Data.Sqlite;

var parsed = CliArgs.Parse(args);
if (parsed.Errors.Count > 0 || parsed.ShowHelp || parsed.ShowVersion)
{
    // All CLI text goes to stderr; stdout carries only stdio protocol frames.
    return CliArgs.Render(parsed, Console.Error);
}

var config = ServerConfig.Build(parsed.Options);

if (parsed.CommandPath.Length > 0)
{
    return await ConfigVerbRunner.RunAsync(parsed, config, Console.Out, Console.Error, Console.In);
}

var app = McpServerSetup.CreateServerHost(config);

var factory = app.Services.GetRequiredService<SqliteConnectionFactory>();
var resolver = app.Services.GetRequiredService<EncryptionKeyResolver>();

EncryptionKeyResolver.ResolvedKey resolved;
try
{
    resolved = resolver.Resolve();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ai-raccoon: {ex.Message}");
    return 1;
}

try
{
    await using var probe = await factory.OpenBankWithKeyAsync(resolved.Passphrase);
}
catch (SqliteException) when (resolved.SourceName == EncryptionSettingsKeys.SourceBitwarden)
{
    Console.Error.WriteLine(
        "ai-raccoon: encryption mismatch: the bank cannot be opened with the bitwarden key — if the secret was rotated, the bank must be rekeyed (run 'ai-raccoon encryption bitwarden')");
    return 1;
}
catch (SqliteException ex) when (resolved.Passphrase is null && ex.SqliteErrorCode == 26)
{
    Console.Error.WriteLine(
        "ai-raccoon: bank is encrypted but no encryption source is configured (set AIRACCOON_DB_PASSPHRASE or run 'ai-raccoon encryption bitwarden')");
    return 1;
}
catch (SqliteException ex) when (resolved.Passphrase is not null && ex.SqliteErrorCode == 26)
{
    Console.Error.WriteLine(
        "ai-raccoon: encryption mismatch: the bank cannot be opened with the configured passphrase — set the correct AIRACCOON_DB_PASSPHRASE or run 'ai-raccoon encryption bitwarden'");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ai-raccoon: {ex.Message}");
    return 1;
}

// Best-effort bundled-model bootstrap (FR-NM-3; see docs/work/features-native-memory/native-memory.feature):
// warn, never fail, when the packaged ONNX is missing.
await EmbeddingBootstrap.EnsureAtStartupAsync(Console.Error, BundledModel.EnsureAsync, CancellationToken.None);

await app.RunAsync(config);
return 0;
