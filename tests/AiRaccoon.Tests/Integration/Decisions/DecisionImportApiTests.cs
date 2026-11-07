using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Infrastructure.Decisions;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Decisions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

/// <summary>The Semantica import routes on a real host: the size limits, the refusal kinds and the access gate.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionImportApiTests : IAsyncLifetime
{
    private const int Limit = 10 * 1024 * 1024;
    private readonly string _project = Guid.CreateVersion7().ToString("D");
    private readonly byte[] _source = SemanticaParsingTests.Fixture("export-graph-json-0.7.0.json");
    private readonly byte[] _mapping = SemanticaParsingTests.Fixture("export-graph-json-0.7.0.mapping.json");
    private DecisionHost _host = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await DecisionHost.StartAsync("decision-import-api", Ct);
        await _host.RegisterAsync(_project, Ct);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private string Route(string verb, string? project = null) => $"/decisions/projects/{project ?? _project}/imports/semantica/{verb}";

    private static byte[] Padded(byte[] bytes, int length)
    {
        var padded = new byte[length];
        bytes.CopyTo(padded, 0);
        Array.Fill(padded, (byte)' ', bytes.Length, length - bytes.Length);
        return padded;
    }

    private Task<HttpResponseMessage> PreviewAsync(byte[] source, byte[] mapping, string? project = null) =>
        _host.Http.PostAsJsonAsync(Route("preview", project), new { source, mapping }, Ct);

    private Task<HttpResponseMessage> ApplyAsync(long revision, string requestId = "import", string? project = null, byte[]? source = null, byte[]? mapping = null) =>
        _host.Http.PostAsJsonAsync(Route("apply", project), new
        {
            sessionId = "session", requestId, source = source ?? _source, mapping = mapping ?? _mapping,
            sourceDigest = SemanticaImportPlan.Digest(source ?? _source), mappingDigest = SemanticaImportPlan.Digest(mapping ?? _mapping),
            expectedProjectRevision = revision
        }, Ct);

    private static async Task<(string Kind, JsonElement Root)> ReadAsync(HttpResponseMessage response)
    {
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
        return (root.TryGetProperty("kind", out var kind) ? kind.GetString()! : "ok", root);
    }

    private async Task RegisterTasksAsync()
    {
        var service = _host.Services.GetRequiredService<IDecisionService>();
        await service.RegisterTaskAsync(_project, new DecisionWrite("semantica-legacy", "s", "t1"), "Legacy", cancellationToken: Ct);
        await service.RegisterTaskAsync(_project, new DecisionWrite("storage-task", "s", "t2"), "Storage", cancellationToken: Ct);
    }

    [RetryFact]
    public async Task ImportRoutes_AcceptMaxSizeAndRefuseOverLimit()
    {
        using var legal = await PreviewAsync(Padded(_source, Limit), Padded(_mapping, Limit));
        legal.StatusCode.ShouldBe(HttpStatusCode.OK, await legal.Content.ReadAsStringAsync(Ct));

        using var bigSource = await PreviewAsync(Padded(_source, Limit + 1), _mapping);
        bigSource.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ReadAsync(bigSource)).Kind.ShouldBe("decision-import-too-large");
        using var bigMapping = await PreviewAsync(_source, Padded(_mapping, Limit + 1));
        (await ReadAsync(bigMapping)).Kind.ShouldBe("decision-import-too-large");

        using var overBody = await PreviewAsync(Padded(_source, Limit + 1048576), Padded(_mapping, Limit + 1048576));
        overBody.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ReadAsync(overBody)).Kind.ShouldBe("decision-import-too-large");
    }

    [RetryFact]
    public async Task ImportRoutes_RefusalKindsAndGate()
    {
        var unregistered = Guid.CreateVersion7().ToString("D");
        (await ReadAsync(await PreviewAsync(_source, _mapping, unregistered))).Kind.ShouldBe("project-not-registered");
        (await ReadAsync(await ApplyAsync(0, project: unregistered))).Kind.ShouldBe("project-not-registered");

        using var empty = await _host.Http.PostAsJsonAsync(Route("preview"), new { }, Ct);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(empty)).Kind.ShouldBe("invalid-argument");
        using var badBase64 = await _host.Http.PostAsync(Route("preview"), new StringContent("""{"source":"!!","mapping":"e30="}""", Encoding.UTF8, "application/json"), Ct);
        (await ReadAsync(badBase64)).Kind.ShouldBe("invalid-argument");
        using var notJson = await PreviewAsync(Encoding.UTF8.GetBytes("nope"), _mapping);
        notJson.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var blocked = await ApplyAsync(0);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadAsync(blocked)).Kind.ShouldBe("decision-import-blocked");

        await RegisterTasksAsync();
        using var stale = await ApplyAsync(0, "stale");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var (staleKind, staleRoot) = await ReadAsync(stale);
        staleKind.ShouldBe("decision-conflict");
        staleRoot.GetProperty("currentProjectRevision").GetInt64().ShouldBe(2);

        await _host.SetModeAsync(_project, "ro", Ct);
        using var denied = await ApplyAsync(2, "denied");
        (await ReadAsync(denied)).Kind.ShouldBe("access-denied");
        using var readOnlyPreview = await PreviewAsync(_source, _mapping);
        readOnlyPreview.StatusCode.ShouldBe(HttpStatusCode.OK);
        var report = (await ReadAsync(readOnlyPreview)).Root.GetProperty("data");
        report.GetProperty("projectRevision").GetInt64().ShouldBe(2);
        report.GetProperty("value").GetProperty("canApply").GetBoolean().ShouldBeTrue();

        await _host.SetModeAsync(_project, "rw", Ct);
        using var applied = await ApplyAsync(2, "ok");
        applied.StatusCode.ShouldBe(HttpStatusCode.OK);
        var outcome = (await ReadAsync(applied)).Root.GetProperty("data");
        outcome.GetProperty("projectRevision").GetInt64().ShouldBe(3);
        outcome.GetProperty("value").GetProperty("counts").GetProperty("decisions").GetInt32().ShouldBe(4);
    }
}
