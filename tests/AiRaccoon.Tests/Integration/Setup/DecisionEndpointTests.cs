using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionEndpointTests : IAsyncLifetime
{
    private readonly string _project = Guid.CreateVersion7().ToString("D");
    private DecisionHost _host = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private HttpClient Http => _host.Http;

    public async ValueTask InitializeAsync()
    {
        _host = await DecisionHost.StartAsync("decision-endpoint", Ct);
        await _host.RegisterAsync(_project, Ct);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [RetryFact]
    public async Task RecordAndGet_UseTheSameProjectAndExposeConflictsAndValidation()
    {
        var root = $"/decisions/projects/{_project}";
        (await Http.PostAsJsonAsync(root + "/tasks", new { taskId = "task", sessionId = "s", requestId = "task", title = "Task" }, Ct)).EnsureSuccessStatusCode();
        var record = await Http.PostAsJsonAsync(root + "/records", new { taskId = "task", sessionId = "s", requestId = "record", title = "Title", category = "architecture", scenario = "Scenario", outcome = "Outcome", reasoning = "Reason" }, Ct);
        record.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await record.Content.ReadAsStringAsync(Ct));
        var id = json.RootElement.GetProperty("data").GetProperty("value").GetProperty("decisionId").GetString();
        var get = await Http.GetAsync(root + "/records/" + id, Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stale = await Http.PostAsJsonAsync(root + "/records/" + id + "/transition", new { taskId = "task", sessionId = "s", requestId = "transition", expectedVersion = 2, targetStatus = "accepted", changeReason = "accept" }, Ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var invalid = await Http.PostAsJsonAsync(root + "/records/" + id + "/transition", new { taskId = "task", sessionId = "s", requestId = "invalid", expectedVersion = 0, targetStatus = "accepted", changeReason = "accept" }, Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Http.GetAsync(root + "/records/missing", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [RetryFact]
    public async Task RegisterTask_UsesProjectGateAndReturnsTask()
    {
        var response = await Http.PostAsJsonAsync($"/decisions/projects/{_project}/tasks",
            new { taskId = "task-1", sessionId = "session-1", requestId = "request-1", title = "Task" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("task-1");
    }

    /// <summary>A read-only project refuses every committed write with the existing access refusal.</summary>
    [RetryTheory]
    [MemberData(nameof(DecisionOperations.WriteTools), MemberType = typeof(DecisionOperations))]
    public async Task WriteRoute_RefusesAReadOnlyProject(string tool)
    {
        await _host.SetModeAsync(_project, "ro", Ct);

        var outcome = await _host.HttpAsync(DecisionOperations.Named(tool), _project, Ct);

        outcome.Status.ShouldBe(422, outcome.Body);
        outcome.Kind.ShouldBe("access-denied", outcome.Body);
    }

    /// <summary>The gate runs before the service on every route: an unregistered project is refused, reads included.</summary>
    [RetryTheory]
    [MemberData(nameof(DecisionOperations.Tools), MemberType = typeof(DecisionOperations))]
    public async Task Route_RefusesAnUnregisteredProject(string tool)
    {
        var outcome = await _host.HttpAsync(DecisionOperations.Named(tool), Guid.CreateVersion7().ToString("D"), Ct);

        outcome.Kind.ShouldBe("project-not-registered", outcome.Body);
    }

    /// <summary>The service sees the gate's canonical id, never the caller's spelling.</summary>
    [RetryFact]
    public async Task NonCanonicalId_ReachesTheServiceCanonical()
    {
        var respelled = $"{{{_project.ToUpperInvariant()}}}";
        var register = await _host.HttpAsync(DecisionOperations.Named("register_decision_task"), respelled, Ct);
        var record = await _host.HttpAsync(DecisionOperations.Named("record_decision"), respelled, Ct);

        register.Kind.ShouldBe("ok", register.Body);
        ProjectIdOf(register.Body).ShouldBe(_project);
        ProjectIdOf(record.Body).ShouldBe(_project);
        var id = JsonDocument.Parse(record.Body).RootElement.GetProperty("data").GetProperty("value").GetProperty("decisionId").GetString();
        (await Http.GetAsync($"/decisions/projects/{_project}/records/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string? ProjectIdOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("projectId").GetString();
}
