using AiRaccoon;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Observability;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Serve;
using Microsoft.Data.Sqlite;
using SQLitePCL;

if (!CliArgs.TryParse(args, out var cliParseResult))
{
    return cliParseResult.RenderTo(Console.Error);
}

var cancellationTokenSource = new CancellationTokenSource();

var serverConfig = cliParseResult.Options.ToServerConfig();
switch (cliParseResult.CommandPath)
{
    case ["serve", "observability"]:
        return await ObservabilityRunner.RunAsync(cliParseResult, Console.Out, Console.Error, cancellationTokenSource.Token);
    case ["serve"]:
        return await ServeRunner.RunAsync(cliParseResult, serverConfig, Console.Out, Console.Error, cancellationTokenSource.Token);
}

if (cliParseResult.CommandPath.Length > 0)
{
    return await CliCommandRunner.RunAsync(cliParseResult, serverConfig, Console.Out, Console.Error, Console.In,
        cancellationTokenSource.Token);
}

// Before the host is built (ADR-0020): the proxy resolves no key, opens no bank and loads no
// model, which is the whole latency argument for making it the default.
if (serverConfig.Transport == McpTransport.Proxy)
{
    return await ProxyRunner.RunAsync(serverConfig, Console.Error, cancellationTokenSource.Token);
}

var app = McpServerSetup.CreateServerHost(serverConfig);
var embeddingAvailability = app.Services.GetRequiredService<EmbeddingAvailability>();
var factory = app.Services.GetRequiredService<SqliteConnectionFactory>();
var resolver = app.Services.GetRequiredService<IEncryptionKeyResolver>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

LogSqliteEngine(logger);

if (!TryResolveEncryptionKey(logger, resolver, out var encryptionKey))
{
    return ExitCode.FailedToResolveEncryptionKey;
}

if (!await TryProbeBankDecryption(logger, factory, encryptionKey, cancellationTokenSource.Token))
{
    return ExitCode.FailedToOpenEncryptedBank;
}

await embeddingAvailability.EnsureEmbeddingAvailabilityAsync(cancellationTokenSource.Token);

return await app.RunAsync(serverConfig, cancellationTokenSource.Token);

static bool TryResolveEncryptionKey(ILogger logger, IEncryptionKeyResolver encryptionKeyResolver, out ResolvedKey resolvedKey)
{
    try
    {
        resolvedKey = encryptionKeyResolver.Resolve();
        return true;
    }
    catch (Exception ex)
    {
        Log.FailedToResolveEncryptionKey(logger, ex);
        resolvedKey = ResolvedKey.None;
        return false;
    }
}

/// <summary>Logs the bundled SQLite engine identity (lib + SQLite3MC train) before key
/// resolution, so it is visible even when startup fails. Diagnostics only — a failure to
/// run the version query must never break startup.</summary>
static void LogSqliteEngine(ILogger logger)
{
    try
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite3mc_version()";
        var engineVersion = (string)cmd.ExecuteScalar()!;
        Log.SqliteEngineVersion(logger, raw.sqlite3_libversion().utf8_to_string(), engineVersion);
    }
    catch (SqliteException)
    {
    }
}

static async Task<bool> TryProbeBankDecryption(ILogger logger, SqliteConnectionFactory sqliteConnectionFactory, ResolvedKey resolvedKey, CancellationToken cancellationToken)
{
    try
    {
        await using var probe = await sqliteConnectionFactory.OpenBankWithKeyAsync(resolvedKey.Passphrase, cancellationToken);
        return true;
    }
    catch (Exception ex)
    {
        Log.FailedToOpenEncryptedBank(logger, resolvedKey.SourceName, ex.Message, ex);
        return false;
    }
}

namespace AiRaccoon
{
    public static partial class Log
    {
        [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Failed to resolve encryption key")]
        public static partial void FailedToResolveEncryptionKey(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Failed to open encrypted bank with {EncryptionSource} encryption source key: {Error}")]
        public static partial void FailedToOpenEncryptedBank(ILogger logger, string encryptionSource, string error, Exception exception);

        [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "ai-raccoon: SQLite engine {LibVersion} ({EngineVersion})")]
        public static partial void SqliteEngineVersion(ILogger logger, string libVersion, string engineVersion);
    }
}
