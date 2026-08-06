using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AiRaccoon.Setup;

/// <summary>
///     The one-shot config-verb path (Program.cs): explicit composition of the bank, watch
///     store and encryption provider family — no DI container, no host, no key probe/embedding
///     bootstrap. Logging goes to stderr (the stdio protocol owns stdout).
/// </summary>
internal static class ConfigVerbRunner
{
    public static async Task<int> RunAsync(CliParseResult parsed, ServerConfig config, TextWriter stdout,
        TextWriter stderr, TextReader stdin, CancellationToken cancellationToken = default)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });
        var logger = loggerFactory.CreateLogger("ConfigCommands");

        var bankPath = SqliteConnectionFactory.BankPathFor(config.Options);
        var bws = new BitwardenCliSecretManager();
        var resolver = EncryptionKeyResolver.Create(bankPath, bws);
        var encryptionState = new EncryptionState(bankPath);
        var env = new EnvEncryptionKeyProvider();
        var bank = new SqliteConnectionFactory(config.Options, resolver);
        var store = new SqliteMemoryStore(bank, TimeProvider.System, new TokenizerChunker(), new EmbeddingService());

        var encryptionCommands = new EncryptionCommands(bank, bws, env, encryptionState, logger);

        return await ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr, stdin,
            cancellationToken, encryptionCommands: encryptionCommands, watchStore: new WatchStore(bank));
    }
}
