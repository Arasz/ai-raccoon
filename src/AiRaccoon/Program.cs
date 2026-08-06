using AiRaccoon;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;

if (!CliArgs.TryParse(args, out var cliParseResult))
{
    return cliParseResult.RenderTo(Console.Error);
}

var cancellationTokenSource = new CancellationTokenSource();

var serverConfig = cliParseResult.Options.ToServerConfig();
if (cliParseResult.CommandPath.Length > 0)
{
    return await ConfigVerbRunner.RunAsync(cliParseResult, serverConfig, Console.Out, Console.Error, Console.In,
        cancellationTokenSource.Token);
}

var app = McpServerSetup.CreateServerHost(serverConfig);
var embeddingAvailability = app.Services.GetRequiredService<EmbeddingAvailability>();
var factory = app.Services.GetRequiredService<SqliteConnectionFactory>();
var resolver = app.Services.GetRequiredService<IEncryptionKeyResolver>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

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
        [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to resolve encryption key")]
        public static partial void FailedToResolveEncryptionKey(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to open encrypted bank with {EncryptionSource} encryption source key: {Error}")]
        public static partial void FailedToOpenEncryptedBank(ILogger logger, string encryptionSource, string error, Exception exception);
    }
}
