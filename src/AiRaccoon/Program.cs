using AiRaccoon.Setup;

var builder = WebApplication.CreateBuilder(args);

builder
    .ConfigureMcpServer()
    .Services.RegisterMemoryServices();

var app = builder.Build().ConfigureMcpEndpoints();

// Provision native extensions now that the container (and its logger) exists; failures are
// logged, not fatal — the first tool call that opens the bank surfaces the missing module.
app.Services.ProvisionExtensions();

await app.RunAsync();
