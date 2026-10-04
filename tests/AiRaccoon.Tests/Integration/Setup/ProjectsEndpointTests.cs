using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Projects;
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
///     The control-plane project directory: name lookup, id check and registration over the
///     token-guarded host, so the CLI never opens the bank for them.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ProjectsEndpointTests : IAsyncLifetime
{
    private const string Token = "projects-endpoint-token";
    private const string Fresh = "0199a1b2-0000-7000-8000-00000000000f";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-projects-endpoint");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, options) { McpToken = Token });
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _client.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, Token);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    [RetryFact]
    public async Task GetByName_OneMatch_Is200WithTheStoredId()
    {
        var ct = TestContext.Current.CancellationToken;
        await _app.Services.GetRequiredService<IProjectRegistry>().RegisterAsync("ai-badger", "ai-badger", ct);

        var response = await _client.GetAsync(ProjectsProtocol.ForName("ai-badger"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ProjectIdsResponse>(ct)).ShouldNotBeNull().Ids.ShouldBe(["ai-badger"]);
    }

    [RetryFact]
    public async Task GetByName_NoMatch_Is200EmptyIds()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(ProjectsProtocol.ForName("nobody"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ProjectIdsResponse>(ct)).ShouldNotBeNull().Ids.ShouldBeEmpty();
    }

    [RetryTheory]
    [InlineData("/projects")]
    [InlineData("/projects?name=")]
    [InlineData("/projects?name=%20")]
    [InlineData("/projects/check")]
    [InlineData("/projects/check?id=%20")]
    public async Task Get_BlankNameOrId_Is400(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RetryFact]
    public async Task Check_UnknownGuid_IsUnknownUnderTheCanonicalId_OnTheWire()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(ProjectsProtocol.ForCheck(Fresh.ToUpperInvariant()), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe($$"""{"status":"unknown","projectId":"{{Fresh}}"}""");
    }

    [RetryFact]
    public async Task Check_Registered_IsKnown()
    {
        var ct = TestContext.Current.CancellationToken;
        await _app.Services.GetRequiredService<IProjectRegistry>().RegisterAsync("ai-badger", null, ct);

        var response = await _client.GetAsync(ProjectsProtocol.ForCheck("ai-badger"), ct);

        (await response.Content.ReadFromJsonAsync<ProjectCheckResponse>(ct))
            .ShouldBe(new ProjectCheckResponse(ProjectIdStatus.Known, "ai-badger"));
    }

    [RetryFact]
    public async Task Post_UnknownGuid_RegistersIt_ThenAnswersAlreadyRegistered()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await _client.PostAsJsonAsync(ProjectsProtocol.Path, new ProjectRegisterRequest(Fresh, "acme"), ct);
        var second = await _client.PostAsJsonAsync(ProjectsProtocol.Path, new ProjectRegisterRequest(Fresh, null), ct);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync(ct)).ShouldBe($$"""{"outcome":"registered","projectId":"{{Fresh}}"}""");
        (await second.Content.ReadFromJsonAsync<ProjectRegisterResponse>(ct))
            .ShouldBe(new ProjectRegisterResponse(ProjectRegistrationOutcome.AlreadyRegistered, Fresh));
        (await _app.Services.GetRequiredService<IProjectRegistry>().IsRegisteredAsync(Fresh, ct)).ShouldBeTrue();
    }

    [RetryFact]
    public async Task Post_UnknownRawText_IsNotAGuid()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(ProjectsProtocol.Path, new ProjectRegisterRequest("acme-raw", null), ct);

        (await response.Content.ReadFromJsonAsync<ProjectRegisterResponse>(ct))
            .ShouldBe(new ProjectRegisterResponse(ProjectRegistrationOutcome.NotAGuid, "acme-raw"));
    }

    [RetryTheory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(Fresh, "")]
    [InlineData(Fresh, "  ")]
    public async Task Post_BlankIdOrName_Is400(string projectId, string? name)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(ProjectsProtocol.Path, new ProjectRegisterRequest(projectId, name), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.Services.GetRequiredService<IProjectRegistry>().IsRegisteredAsync(Fresh, ct)).ShouldBeFalse();
    }

    [RetryFact]
    public async Task Post_NameOfTwoHundredOneCharacters_Is400WritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(ProjectsProtocol.Path,
            new ProjectRegisterRequest(Fresh, new string('a', 201)), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.Services.GetRequiredService<IProjectRegistry>().IsRegisteredAsync(Fresh, ct)).ShouldBeFalse();
    }

    [RetryTheory]
    [InlineData("acme\u001b")]
    [InlineData("ac\rme")]
    public async Task Post_NameWithControlCharacter_Is400WithAPrintableEcho(string name)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(ProjectsProtocol.Path, new ProjectRegisterRequest(Fresh, name), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.Any(char.IsControl).ShouldBeFalse(body);
        (await _app.Services.GetRequiredService<IProjectRegistry>().IsRegisteredAsync(Fresh, ct)).ShouldBeFalse();
    }

    [RetryFact]
    public async Task Post_NameOfTwoHundredCharacters_Registers()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = new string('a', 200);

        var response = await _client.PostAsJsonAsync(ProjectsProtocol.Path, new ProjectRegisterRequest(Fresh, name), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.Services.GetRequiredService<IProjectRegistry>().IsRegisteredAsync(Fresh, ct)).ShouldBeTrue();
    }

    [RetryFact]
    public async Task WithoutTheToken_EveryRouteIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };

        (await anonymous.GetAsync(ProjectsProtocol.ForName("acme"), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(ProjectsProtocol.ForCheck(Fresh), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync(ProjectsProtocol.Path, new ProjectRegisterRequest(Fresh, null), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.Services.GetRequiredService<IProjectRegistry>().IsRegisteredAsync(Fresh, ct)).ShouldBeFalse();
    }
}
