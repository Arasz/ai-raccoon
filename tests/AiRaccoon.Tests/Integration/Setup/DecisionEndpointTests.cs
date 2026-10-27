using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionEndpointTests : IAsyncLifetime
{
    private const string Project = "4f1d2c3b-5a6e-4b7c-8d9e-0f1a2b3c4d5e";
    private const string Token = "decision-endpoint-token";
    private readonly string _root = TestData.CreateTempRoot("decision-endpoint");
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Scope = InstallScope.User };
        _app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, options) { McpToken = Token });
        await _app.StartAsync(Ct);
        await _app.Services.GetRequiredService<IProjectRegistry>().RegisterAsync(Project, null, Ct);
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _http.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, Token);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        TestData.DeleteTempRoot(_root);
    }

    [RetryFact]
    public async Task RegisterTask_UsesProjectGateAndReturnsTask()
    {
        var response = await _http.PostAsJsonAsync($"/decisions/projects/{Project}/tasks",
            new { taskId = "task-1", sessionId = "session-1", requestId = "request-1", title = "Task" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("task-1");
    }
}
