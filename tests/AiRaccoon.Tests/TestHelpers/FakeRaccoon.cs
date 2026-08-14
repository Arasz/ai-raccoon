using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A listener that looks like an ai-raccoon server to the probe and to /observability, but
///     answers /shutdown however the test says and never actually stops.
/// </summary>
internal sealed class FakeRaccoon : IAsyncDisposable
{
    private const string JsonRpcRefusal = """{"jsonrpc":"2.0","id":null,"error":{"code":-32001,"message":"nope"}}""";

    private readonly WebApplication _app;

    private FakeRaccoon(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public int ShutdownRequests { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Starts on <paramref name="port"/>, answering /shutdown with <paramref name="shutdownStatus"/>.
    /// <paramref name="name"/> is what /observability claims to be; a null <paramref name="version"/>
    /// leaves the field out entirely, like a server predating ADR-0022.</summary>
    public static async Task<FakeRaccoon> StartAsync(int port, HttpStatusCode shutdownStatus,
        CancellationToken cancellationToken, string name = "ai-raccoon", string? version = "0.0.0-fake")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var fake = new FakeRaccoon(app, port);

        var otlp = new { enabled = false, endpoint = (string?)null, protocol = (string?)null };
        app.MapPost("/mcp", () => Results.Text(JsonRpcRefusal, "application/json", null, StatusCodes.Status401Unauthorized));
        app.MapGet("/observability", () => version is null
            ? Results.Json(new { name, pid = Environment.ProcessId, otlp })
            : Results.Json(new { name, version, pid = Environment.ProcessId, otlp }));
        app.MapPost("/shutdown", () =>
        {
            fake.ShutdownRequests++;
            return Results.StatusCode((int)shutdownStatus);
        });

        await app.StartAsync(cancellationToken);
        return fake;
    }
}
