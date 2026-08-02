

// Transport selection: the "http" launch profile sets MCP_TRANSPORT=http to run the
// Streamable HTTP transport; anything else (default) uses stdio, which is what MCP
// clients expect when launching the server as a subprocess.
if (McpTransportSelector.UseHttp(Environment.GetEnvironmentVariable("MCP_TRANSPORT")))
{
    var builder = WebApplication.CreateBuilder(args);

    // Add the MCP services: the transport to use (http) and the tools to register.
    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options =>
        {
            // Stateless mode is recommended for servers that don't need
            // server-to-client requests like sampling or elicitation.
            options.Stateless = true;
        })
        .WithTools<RandomNumberTools>();

    var app = builder.Build();
    app.MapMcp("/mcp");
    app.Run();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);

    // Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    // Add the MCP services: the transport to use (stdio) and the tools to register.
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<RandomNumberTools>();

    await builder.Build().RunAsync();
}
