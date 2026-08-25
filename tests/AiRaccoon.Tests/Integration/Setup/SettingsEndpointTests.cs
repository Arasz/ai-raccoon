using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     WP7 §5.3 (docs/plans/2026-08-16-bank-open-cost-implementation.md): the control-plane
///     settings endpoint, which serves reads and writes alike so a subsystem has one
///     implementation rather than two. Modelled on /shutdown — an endpoint, not an MCP tool, so
///     the single-config-channel constraint on the *tool* surface is untouched.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SettingsEndpointTests : IAsyncLifetime
{
    private const string Token = "settings-endpoint-token";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-settings-endpoint");
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
    public async Task PutThenGet_RoundTripsTheValue()
    {
        (await PutAsync("sweep.threshold", "0.7")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await _client.GetAsync("/settings?key=sweep.threshold", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SettingValue>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Value.ShouldBe("0.7");
    }

    [RetryFact]
    public async Task Get_ForAnAbsentKey_IsNotFound()
    {
        var response = await _client.GetAsync("/settings?key=nothing.here", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [RetryFact]
    public async Task GetByPrefix_ReturnsOnlyMatchingRows()
    {
        await PutAsync("queryGuard.enabled.global", "false");
        await PutAsync("queryGuard.shadow.global", "true");
        await PutAsync("sweep.threshold", "0.3");

        var response = await _client.GetAsync("/settings?prefix=queryGuard.", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = (await response.Content.ReadFromJsonAsync<SettingRows>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Rows;
        rows.Count.ShouldBe(2);
        rows["queryGuard.enabled.global"].ShouldBe("false");
        rows["queryGuard.shadow.global"].ShouldBe("true");
    }

    [RetryFact]
    public async Task GetByPrefix_WithNoMatch_IsAnEmptySet_NotAnError()
    {
        var response = await _client.GetAsync("/settings?prefix=nothing.", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SettingRows>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Rows.ShouldBeEmpty();
    }

    [RetryFact]
    public async Task Delete_RemovesTheRow()
    {
        await PutAsync("noise.enabled.global", "false");

        var deleted = await _client.DeleteAsync("/settings?key=noise.enabled.global", TestContext.Current.CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync("/settings?key=noise.enabled.global", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Deleting what is not there is how every settings handler already behaves.</summary>
    [RetryFact]
    public async Task Delete_ForAnAbsentKey_Succeeds()
    {
        var response = await _client.DeleteAsync("/settings?key=never.written", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [RetryTheory]
    [InlineData("/settings")]
    [InlineData("/settings?key=a&prefix=b")]
    [InlineData("/settings?key=")]
    public async Task Get_WithoutExactlyOneSelector_IsABadRequest(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RetryFact]
    public async Task Put_WithoutAKey_IsABadRequest()
    {
        var response = await _client.PutAsJsonAsync("/settings", new SettingWrite("", "v"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    ///     The endpoint carries secrets (sync credentials, the OpenAI key), so its refusal without
    ///     the token is asserted here too, not only by the route-table guard.
    /// </summary>
    [RetryFact]
    public async Task WithoutTheToken_EveryVerbIsRefused()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };

        (await anonymous.GetAsync("/settings?key=a", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync("/settings", new SettingWrite("a", "b"), TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync("/settings?key=a", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    ///     #472: a direct (non-CLI) caller must see the store's refusal reason in the response, not
    ///     a bare 500 — the CLI never reaches this because SettingsCommands pre-checks the manifest
    ///     itself before ever calling the endpoint.
    /// </summary>
    [RetryFact]
    public async Task PostModelCode_MissingManifest_IsABadRequest_WithTheReasonInTheBody()
    {
        var dir = Path.Combine(_dataRoot, "code-model-missing-manifest");
        Directory.CreateDirectory(dir);

        var response = await _client.PostAsJsonAsync(SettingsProtocol.ModelCodePath,
            new ModelCodeActivationRequest(dir), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain(EmbeddingManifest.FileName);
    }

    /// <summary>vec-code-unfix-dim: dimensions are no longer a refusal leg — a 1024 manifest
    /// activates via the endpoint; the chunk-budget gate (next test) is the remaining refusal.</summary>
    [RetryFact]
    public async Task PostModelCode_Non768Manifest_Activates()
    {
        var dir = Path.Combine(_dataRoot, "code-model-1024");
        TestData.SeedCodeManifestDirectory(dir, 1024);

        var response = await _client.PostAsJsonAsync(SettingsProtocol.ModelCodePath,
            new ModelCodeActivationRequest(dir), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>#472: same mapping for the third refusal leg — a manifest whose context window
    /// resolves to a chunk budget narrower than the code chunker's fixed budget (#422).</summary>
    [RetryFact]
    public async Task PostModelCode_ManifestWindowNarrowerThanTheChunkerBudget_IsABadRequest_WithTheReasonInTheBody()
    {
        var dir = Path.Combine(_dataRoot, "code-model-narrow-ctx");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var manifest = File.ReadAllText(
                TestData.RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1.json"))
            .Replace("\"contextWindowTokens\": 512", "\"contextWindowTokens\": 128");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest);

        var response = await _client.PostAsJsonAsync(SettingsProtocol.ModelCodePath,
            new ModelCodeActivationRequest(dir), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("126");
        body.ShouldContain(CodeChunker.DefaultBudget.ToString());
    }

    /// <summary>
    ///     Found by the 1.32.0 post-publish check after #476: the chunk-budget leg is the one
    ///     refusal SettingsCommands does not pre-check locally, so it is the only leg that actually
    ///     drives ServerSettingsStore.ActivateCodeEngineAsync's own 400 handling. Before the fix,
    ///     Ensure() fell through to EnsureSuccessStatusCode() and this threw a bare
    ///     HttpRequestException with no reason in it.
    /// </summary>
    [RetryFact]
    public async Task ServerSettingsStore_ActivateCodeEngine_OnAChunkBudgetRefusal_ThrowsWithTheReason()
    {
        var dir = Path.Combine(_dataRoot, "code-model-narrow-ctx-cli");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var manifest = File.ReadAllText(
                TestData.RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1.json"))
            .Replace("\"contextWindowTokens\": 512", "\"contextWindowTokens\": 128");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest);
        var store = new ServerSettingsStore(new HttpClient { BaseAddress = new Uri(_app.Urls.First()) }, Token);

        var ex = await Should.ThrowAsync<CodeEngineActivationRefusedException>(
            () => store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(CodeChunker.DefaultBudget.ToString());
    }

    private Task<HttpResponseMessage> PutAsync(string key, string value) =>
        _client.PutAsJsonAsync("/settings", new SettingWrite(key, value), TestContext.Current.CancellationToken);
}
