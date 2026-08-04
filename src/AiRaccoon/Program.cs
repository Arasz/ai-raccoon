using AiRaccoon.Setup;

var parsed = CliArgs.Parse(args);
if (parsed.Errors.Count > 0 || parsed.ShowHelp || parsed.ShowVersion)
{
    // All CLI text goes to stderr; stdout carries only stdio protocol frames.
    return CliArgs.Render(parsed, Console.Error);
}

var config = ServerConfig.Build(parsed.Options, Environment.GetEnvironmentVariable);
var builder = WebApplication.CreateBuilder([]); // args already consumed by CliArgs

builder
    .ConfigureMcpServer(config.Transport)
    .Services.RegisterMemoryServices(config.Options);

var app = builder.Build().ConfigureMcpEndpoints(config.Transport);

await app.RunAsync();
return 0;
