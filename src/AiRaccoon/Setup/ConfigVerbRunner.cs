using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;

namespace AiRaccoon.Setup;

/// <summary>
///     One-shot config-verb run against the bank (single config channel): shares the server's
///     bank resolution so --data-root/--install-scope apply. Returns the process exit code.
/// </summary>
internal static class ConfigVerbRunner
{
    internal static Task<int> RunAsync(CliParseResult parsed, ServerConfig config,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
        CancellationToken cancellationToken = default)
    {
        var bws = new BwsProcessRunner();
        var bank = new SqliteConnectionFactory(config.Options, new EncryptionKeyResolver(
            config.Options, new EnvEncryptionKeyProvider(), bws));
        var store = new SqliteMemoryStore(bank, TimeProvider.System, new TokenizerChunker(), new EmbeddingService());
        return ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr,
            stdin, cancellationToken, bank: bank, bws: bws, watchStore: new WatchStore(bank));
    }
}
