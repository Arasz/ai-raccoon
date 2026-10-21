using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     ADR-0075 amendment: `watch add|remove|status` reach the server's watch service through the
///     same gate as memory_watch_add/remove/status, driven here through the CLI's own client.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WatchEndpointTests : IAsyncLifetime
{
    private const string Token = "watch-endpoint-token";
    private const string Project = "4f1d2c3b-5a6e-4b7c-8d9e-0f1a2b3c4d5e";
    private const string Other = "5e2d3c4b-6a7f-4c8d-9e0f-1a2b3c4d5e6f";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-watch-endpoint");
    private readonly string _tree = TestData.CreateTempRoot("ai-raccoon-watch-endpoint-tree");
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private ServerSettingsStore _client = null!;

    private string Repo => IngestPath.Normalize(_tree);

    private string Sub => IngestPath.Normalize(Path.Combine(_tree, "sub"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_tree, "sub"));
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, options) { McpToken = Token });
        await _app.StartAsync(Ct);
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _http.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, Token);
        _client = new ServerSettingsStore(new HttpClient { BaseAddress = new Uri(_app.Urls.First()) }, Token,
            CliSettingsBackend.RequestDeadline);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
        TestData.DeleteTempRoot(_tree);
    }

    [Fact]
    public async Task SharedWatch_UsesTheNormalEnableScopeAndAccessGates_WithoutRegistration()
    {
        var disabled = await Should.ThrowAsync<WatchRefusedException>(() => _client.AddAsync("shared", Repo, Ct));
        disabled.Kind.ShouldBe("watching-disabled");
        await SetAsync(WatchConfigKeys.EnabledProject("shared"), "true");
        var outside = await Should.ThrowAsync<WatchRefusedException>(() => _client.AddAsync("shared", Repo, Ct));
        outside.Kind.ShouldBe("path-outside-scope");
        await SetAsync(IngestScopeKeys.ScopeProject("shared"), IngestScopeKeys.Serialize([Repo]));

        var added = await _client.AddAsync("shared", Repo, Ct);

        added.ProjectId.ShouldBe("shared");
        (await _client.StatusAsync("shared", Ct)).ShouldHaveSingleItem();
        var memory = _app.Services.GetRequiredService<IMemoryStore>();
        (await memory.GetSettingAsync(SharedProject.WatchActivationKey, Ct)).ShouldBe("1");
        await SetAsync("access.mode.project:shared", "ro");
        var denied = await Should.ThrowAsync<WatchRefusedException>(() => _client.AddAsync("shared", Sub, Ct));
        denied.Kind.ShouldBe("access-denied");
    }

    [RetryFact]
    public async Task Add_RegistersTheWatch_AndStatusReportsItsLiveState()
    {
        await AllowAsync(Project);

        var added = await _client.AddAsync(Project, Repo, Ct);

        added.ProjectId.ShouldBe(Project);
        added.Path.ShouldBe(Repo);
        added.Pruned.ShouldBeEmpty();
        added.AbsorbedBy.ShouldBeNull();
        var status = (await _client.StatusAsync(Project, Ct)).ShouldHaveSingleItem();
        status.ProjectId.ShouldBe(Project);
        status.Path.ShouldBe(Repo);
        (await _app.Services.GetRequiredService<IWatchRegisteredStore>().ListWatchesAsync(Ct))
            .ShouldContain(w => w.ProjectId == Project && w.Path == Repo);
    }

    [RetryFact]
    public async Task Add_ABroaderPath_PrunesTheWatchItContains()
    {
        await AllowAsync(Project);
        await _client.AddAsync(Project, Sub, Ct);

        var added = await _client.AddAsync(Project, Repo, Ct);

        added.Pruned.ShouldBe([Sub]);
        added.AbsorbedBy.ShouldBeNull();
    }

    [RetryFact]
    public async Task Add_TheSamePathAgain_ReportsAbsorbedBy()
    {
        await AllowAsync(Project);
        await _client.AddAsync(Project, Repo, Ct);

        var added = await _client.AddAsync(Project, Repo, Ct);

        added.AbsorbedBy.ShouldBe(Repo);
        added.Pruned.ShouldBeEmpty();
    }

    [RetryFact]
    public async Task Remove_DropsTheRegistration()
    {
        await AllowAsync(Project);
        await _client.AddAsync(Project, Repo, Ct);

        var removed = await _client.RemoveAsync(Project, Repo, Ct);

        removed.ShouldBe(new WatchRemoveResponse(Project, Repo, true));
        (await _client.StatusAsync(Project, Ct)).ShouldBeEmpty();
    }

    [RetryFact]
    public async Task Remove_AnUnwatchedPath_ReportsNothingRemoved()
    {
        await AllowAsync(Project);

        var removed = await _client.RemoveAsync(Project, Repo, Ct);

        removed.ShouldBe(new WatchRemoveResponse(Project, Repo, false));
    }

    [RetryFact]
    public async Task Status_WithoutAProjectId_ListsEveryProjectsWatches()
    {
        await AllowAsync(Project);
        await AllowAsync(Other);
        await _client.AddAsync(Project, Sub, Ct);
        await _client.AddAsync(Other, Repo, Ct);

        var all = await _client.StatusAsync(null, Ct);

        all.Select(s => (s.ProjectId, s.Path)).ShouldBe([(Project, Sub), (Other, Repo)], ignoreOrder: true);
    }

    [RetryFact]
    public async Task Add_WithWatchingDisabled_IsRefused()
    {
        await RegisterAsync(Project);
        await SetAsync(IngestScopeKeys.ScopeProject(Project), IngestScopeList.ToJson([Repo]));

        var refused = await Should.ThrowAsync<WatchRefusedException>(() => _client.AddAsync(Project, Repo, Ct));

        refused.Kind.ShouldBe("watching-disabled");
        refused.Message.ShouldContain($"ai-raccoon settings watch enable {Project} true");
    }

    [RetryFact]
    public async Task Add_OutsideTheScope_IsRefused()
    {
        await RegisterAsync(Project);
        await SetAsync(WatchConfigKeys.EnabledProject(Project), "true");

        var refused = await Should.ThrowAsync<WatchRefusedException>(() => _client.AddAsync(Project, Repo, Ct));

        refused.Kind.ShouldBe("path-outside-scope");
        refused.Message.ShouldContain($"ai-raccoon settings ingest scope add {Project} {Repo}");
    }

    [RetryFact]
    public async Task Add_AMissingPath_IsRefused()
    {
        await AllowAsync(Project);

        var refused = await Should.ThrowAsync<WatchRefusedException>(() =>
            _client.AddAsync(Project, Path.Combine(Repo, "missing"), Ct));

        refused.Kind.ShouldBe("path-not-found");
    }

    [RetryFact]
    public async Task Add_APathInsideAnExistingWatch_IsRefused_NamingTheCoveringWatch()
    {
        await AllowAsync(Project);
        await _client.AddAsync(Project, Repo, Ct);

        var refused = await Should.ThrowAsync<WatchRefusedException>(() => _client.AddAsync(Project, Sub, Ct));

        refused.Kind.ShouldBe("watch-overlap");
        refused.CoveringPath.ShouldBe(Repo);
        refused.Message.ShouldContain(Repo);
    }

    [RetryFact]
    public async Task Add_ForAnUnregisteredProject_IsRefused_WithTheRegisterFix()
    {
        await SetAsync(WatchConfigKeys.EnabledGlobal, "true");
        await SetAsync(IngestScopeKeys.ScopeGlobal, IngestScopeList.ToJson([Repo]));

        var refused = await Should.ThrowAsync<WatchRefusedException>(() => _client.AddAsync(Project, Repo, Ct));

        refused.Kind.ShouldBe("project-not-registered");
        refused.Message.ShouldContain($"ai-raccoon project id register {Project}");
        (await _app.Services.GetRequiredService<IWatchRegisteredStore>().ListWatchesAsync(Ct)).ShouldBeEmpty();
    }

    [RetryFact]
    public async Task Status_ForAnUnregisteredProject_IsRefused()
    {
        var refused = await Should.ThrowAsync<WatchRefusedException>(() => _client.StatusAsync(Project, Ct));

        refused.Kind.ShouldBe("project-not-registered");
    }

    [RetryFact]
    public async Task Refusal_IsA422ProblemDetails_WithTheKindAsAnExtension()
    {
        await RegisterAsync(Project);

        var response = await _http.PostAsJsonAsync(WatchProtocol.Path, new WatchAddRequest(Project, Repo), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
        problem.GetProperty("status").GetInt32().ShouldBe(422);
        problem.GetProperty("kind").GetString().ShouldBe("watching-disabled");
        problem.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [RetryFact]
    public async Task WithoutTheToken_IsRefused()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };

        (await anonymous.GetAsync(WatchProtocol.Path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task AllowAsync(string projectId)
    {
        await RegisterAsync(projectId);
        await SetAsync(WatchConfigKeys.EnabledProject(projectId), "true");
        await SetAsync(IngestScopeKeys.ScopeProject(projectId), IngestScopeList.ToJson([Repo]));
    }

    private Task RegisterAsync(string projectId) =>
        _app.Services.GetRequiredService<IProjectRegistry>().RegisterAsync(projectId, null, Ct);

    private Task SetAsync(string key, string value) =>
        _app.Services.GetRequiredService<IMemoryStore>().SetSettingAsync(key, value, Ct);
}
