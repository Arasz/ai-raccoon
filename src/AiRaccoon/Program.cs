using AiRaccoon.Setup;

var builder = WebApplication.CreateBuilder(args);

builder
    .ConfigureMcpServer()
    .Services.RegisterMemoryServices();

var app = builder.Build().ConfigureMcpEndpoints();

await app.RunAsync();
