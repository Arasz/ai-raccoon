using AiRaccoon.Setup;

var builder = WebApplication.CreateBuilder(args);

builder
    .ConfigureMcpServer()
    .Services.RegisterMemoryServices();

await builder.Build().ConfigureMcpEndpoints().RunAsync();
