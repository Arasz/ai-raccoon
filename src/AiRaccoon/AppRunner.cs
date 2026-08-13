using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Setup.Cli.Render;
using AiRaccoon.Setup.Extensions;
using AiRaccoon.Setup.Models;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace AiRaccoon;

public sealed partial class AppRunner
{
    private readonly CancellationTokenSource _cts = new();
    private readonly StandardStreams _streams = new(Console.In, Console.Out, Console.Error);

    private CancellationToken Token => _cts.Token;

    public async Task<int> Run(string[] args)
    {
        if (GetCliInput(args) is not { } cliInput)
        {
            return ExitCode.FailedToParseCliArgs;
        }

        if (cliInput.IsCommandInput)
        {
            return await RunCliCommand(cliInput);
        }

        if (cliInput.IsProxyInput)
        {
            return await RunProxy(cliInput);
        }

        return await DirectRunAsync(cliInput);
    }

    private async Task<int> DirectRunAsync(CliInput cliInput)
    {
        var app = McpServerSetup.CreateServerHost(cliInput.ServerConfig);
        var embeddingAvailability = app.Services.GetRequiredService<IEmbeddingAvailability>();
        var factory = app.Services.GetRequiredService<ISqliteConnectionFactory>();
        var resolver = app.Services.GetRequiredService<IEncryptionKeyResolver>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<AppRunner>();

        LogSqliteEngine(logger);

        if (!TryResolveEncryptionKey(logger, resolver, out var encryptionKey))
        {
            return ExitCode.FailedToResolveEncryptionKey;
        }

        if (!await TryProbeBankDecryption(logger, factory, encryptionKey, Token))
        {
            return ExitCode.FailedToOpenEncryptedBank;
        }

        await embeddingAvailability.EnsureEmbeddingAvailabilityAsync(Token);

        return await app.RunAsync(cliInput.ServerConfig, Token);

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

        static async Task<bool> TryProbeBankDecryption(ILogger logger, ISqliteConnectionFactory sqliteConnectionFactory, ResolvedKey resolvedKey, CancellationToken cancellationToken)
        {
            var probeUsingEncryptionKey = await sqliteConnectionFactory.ProbeUsingEncryptionKey(resolvedKey.Passphrase, cancellationToken);
            if (probeUsingEncryptionKey.IsCorrectKey)
            {
                return true;
            }

            Log.FailedToOpenEncryptedBank(logger, resolvedKey.SourceName, probeUsingEncryptionKey.Exception);
            return false;
        }

        static bool TryResolveEncryptionKey(ILogger logger, IEncryptionKeyResolver encryptionKeyResolver, out ResolvedKey resolvedKey)
        {
            var probeResolvingEncryptionKey = encryptionKeyResolver.ProbeResolvingEncryptionKey();
            if (probeResolvingEncryptionKey.IsSuccess)
            {
                resolvedKey = probeResolvingEncryptionKey.Key;
                return true;
            }

            Log.FailedToResolveEncryptionKey(logger, probeResolvingEncryptionKey.Exception);
            resolvedKey = ResolvedKey.None;
            return false;
        }
    }


    private CliInput? GetCliInput(string[] args)
    {
        if (!CliArgs.TryParse(args, out var cliInput))
        {
            return null;
        }

        cliInput?.RenderTo(_streams);
        return cliInput;
    }

    private async Task<int> RunCliCommand(CliInput cliInput)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        services.RegisterCoreMemoryServices(cliInput.ServerConfig.Options);
        services.RegisterCommands();
        await using var providder = services.BuildServiceProvider();
        var configCommands = providder.GetRequiredService<ConfigCommands>();
        return await configCommands.RunAsync(cliInput, _streams, Token);
    }

    private async Task<int> RunProxy(CliInput cliInput)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        services.RegisterCoreMemoryServices(cliInput.ServerConfig.Options);
        services.RegisterProxyServices();
        await using var providder = services.BuildServiceProvider();
        var proxyRunner = providder.GetRequiredService<IProxyRunner>();
        return await proxyRunner.RunAsync(cliInput.ServerConfig, _streams, Token);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Failed to resolve encryption key")]
        public static partial void FailedToResolveEncryptionKey(ILogger logger, Exception? exception);

        [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Failed to open encrypted bank with {EncryptionSource} encryption source key")]
        public static partial void FailedToOpenEncryptedBank(ILogger logger, string encryptionSource, Exception? exception);

        [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "ai-raccoon: SQLite engine {LibVersion} ({EngineVersion})")]
        public static partial void SqliteEngineVersion(ILogger logger, string libVersion, string engineVersion);
    }
}
