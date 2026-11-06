using System.Net.Http.Json;
using System.Text.Json;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     One token-guarded web host serving both the decision HTTP routes and the MCP endpoint, so a
///     test can send the same operation through each adapter and compare what comes back. It holds the
///     env gate as a reader from start until the host stops (docs/adr/0066).
/// </summary>
public sealed class DecisionHost : IAsyncDisposable
{
    /// <summary>What a call answered: <c>ok</c> or the refusal kind, plus the body text.</summary>
    public sealed record Outcome(string Kind, string Body, int Status = 0);

    internal const string Token = "decision-host-token";
    private readonly string _root;
    private readonly bool _ownsRoot;
    private readonly WebApplication _app;
    private readonly IAsyncDisposable _envGate;
    private readonly List<McpClient> _clients = [];

    private DecisionHost(string root, bool ownsRoot, WebApplication app, HttpClient http, IAsyncDisposable envGate)
    {
        _root = root;
        _ownsRoot = ownsRoot;
        _app = app;
        _envGate = envGate;
        Http = http;
    }

    public HttpClient Http { get; }

    public IServiceProvider Services => _app.Services;

    public static Task<DecisionHost> StartAsync(string name, CancellationToken cancellationToken) =>
        StartAsync(TestData.CreateTempRoot(name), true, cancellationToken);

    /// <summary>A host over an existing data root the caller owns: disposing it stops the server and keeps the bank.</summary>
    public static Task<DecisionHost> OpenAsync(string root, CancellationToken cancellationToken) =>
        StartAsync(root, false, cancellationToken);

    private static async Task<DecisionHost> StartAsync(string root, bool ownsRoot, CancellationToken cancellationToken)
    {
        var envGate = await TestData.HoldEnvGateAsync(cancellationToken);
        WebApplication? app = null;
        try
        {
            var options = new InfrastructureOptions { DataRoot = root, Scope = InstallScope.User };
            app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, options) { McpToken = Token });
            await app.StartAsync(cancellationToken);
            var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            http.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, Token);
            return new DecisionHost(root, ownsRoot, app, http, envGate);
        }
        catch
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }

            await envGate.DisposeAsync();
            throw;
        }
    }

    public Task RegisterAsync(string projectId, CancellationToken cancellationToken) =>
        _app.Services.GetRequiredService<IProjectRegistry>().RegisterAsync(projectId, null, cancellationToken);

    public Task SetModeAsync(string projectId, string mode, CancellationToken cancellationToken) =>
        _app.Services.GetRequiredService<IMemoryStore>().SetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), mode, cancellationToken);

    public async Task<McpClient> McpAsync(CancellationToken cancellationToken)
    {
        var httpClient = new HttpClient { BaseAddress = Http.BaseAddress };
        httpClient.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, Token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "decision-host",
                Endpoint = new Uri(Http.BaseAddress!, "mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            true);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        _clients.Add(client);
        return client;
    }

    public async Task<Outcome> HttpAsync(DecisionOperation operation, string projectId, CancellationToken cancellationToken)
    {
        using var response = operation.Method == "GET"
            ? await Http.GetAsync(operation.Path(projectId), cancellationToken)
            : await Http.PostAsJsonAsync(operation.Path(projectId), operation.Body(), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new Outcome("ok", body, (int)response.StatusCode);
        }

        var kind = $"http-{(int)response.StatusCode}";
        if (body.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("kind", out var value))
            {
                kind = value.GetString()!;
            }
        }

        return new Outcome(kind, body, (int)response.StatusCode);
    }

    public async Task<Outcome> McpAsync(McpClient client, string tool, IReadOnlyDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: cancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return result.IsError == true ? new Outcome(text.Split(':')[0], text) : new Outcome("ok", text);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        Http.Dispose();
        try
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
        finally
        {
            await _envGate.DisposeAsync();
        }

        if (_ownsRoot)
        {
            TestData.DeleteTempRoot(_root);
        }
    }
}
