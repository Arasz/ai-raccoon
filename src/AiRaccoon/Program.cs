using AiRaccoon.Setup;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Chunking;

var parsed = CliArgs.Parse(args);
if (parsed.Errors.Count > 0 || parsed.ShowHelp || parsed.ShowVersion)
{
    // All CLI text goes to stderr; stdout carries only stdio protocol frames.
    return CliArgs.Render(parsed, Console.Error);
}

var config = ServerConfig.Build(parsed.Options);

if (parsed.CommandPath.Length > 0)
{
    // Config verbs run as one-shot processes against the bank (single config channel);
    // they share the server's bank resolution so --data-root/--install-scope apply.
    var store = new SqliteMemoryStore(
        new SqliteConnectionFactory(config.Options, new EnvEncryptionKeyProvider()),
        TimeProvider.System, new TokenizerChunker(), new EmbeddingService());
    return await ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, store, Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder([]); // args already consumed by CliArgs

// Ruling 3: appsettings.json is removed — the settings table is the single runtime
// channel, so the host's dormant config sources are cleared.
builder.Configuration.Sources.Clear();

builder
    .ConfigureMcpServer(config.Transport)
    .Services.RegisterMemoryServices(config.Options);

var app = builder.Build().ConfigureMcpEndpoints(config.Transport);

await app.RunAsync();
return 0;
