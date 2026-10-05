using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Tests.Unit.Projects;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class SettingsEndpointTests : IAsyncLifetime
{
    private const string Token = "settings-endpoint-token";
    private const string Unknown = "0199a1b2-0000-7000-8000-0000000000c2";
    private const string Winner = "0199a1b2-0000-7000-8000-0000000000c3";
    private const string Loser = "0199a1b2-0000-7000-8000-0000000000c4";
    private const string Retired = "0199a1b2-0000-7000-8000-0000000000c5";

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
        ProjectIdAliasMap.ResetDefault();
    }

    [RetryTheory]
    [InlineData("ingest.scope.", "[\"/machine\"]")]
    [InlineData("watch.enabled.", "true")]
    [InlineData("watch.concurrency.", "4")]
    public async Task AliasToGlobal_CannotReadWriteOrDeleteMachineSettings(string prefix, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = _app.Services.GetRequiredService<ISettingsStore>();
        await settings.SetSettingAsync(prefix + "global", value, ct);
        ProjectIdAliasMap.ReplaceDefault(new ProjectIdAliasMap(
            [new ProjectIdAliasEntry(Loser, "global")], ["global"], []));

        (await _client.GetAsync("/settings?key=" + prefix + Loser, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PutAsync(prefix + Loser, value)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.DeleteAsync("/settings?key=" + prefix + Loser, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await settings.GetSettingAsync(prefix + "global", ct)).ShouldBe(value);
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
        var store = new ServerSettingsStore(new HttpClient { BaseAddress = new Uri(_app.Urls.First()) }, Token, CliSettingsBackend.RequestDeadline);

        var ex = await Should.ThrowAsync<CodeEngineActivationRefusedException>(
            () => store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(CodeChunker.DefaultBudget.ToString());
    }

    /// <summary>
    ///     The endpoint refuses an unusable base-url the same way the CLI does (ADR-0107 PC.1), before
    ///     any setting is written, so a direct caller cannot open a migration nothing can reach.
    /// </summary>
    [RetryTheory]
    [InlineData("not-a-url")]
    [InlineData("localhost:8080")]
    public async Task PostModel_BaseUrlIsNotAUsableHttpUrl_IsABadRequest_WithNothingPersisted(string baseUrl)
    {
        var response = await _client.PostAsJsonAsync(SettingsProtocol.ModelPath,
            new ModelMigrationRequest("openai", "some-model", baseUrl), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var rowsResponse = await _client.GetAsync("/settings?prefix=embedding.", TestContext.Current.CancellationToken);
        (await rowsResponse.Content.ReadFromJsonAsync<SettingRows>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Rows.ShouldBeEmpty("a refused base-url must not persist settings or open a migration");
    }

    /// <summary>A usable https base-url is unaffected by the base-url guard.</summary>
    [RetryFact]
    public async Task PostModel_AUsableHttpsBaseUrl_Activates()
    {
        var response = await _client.PostAsJsonAsync(SettingsProtocol.ModelPath,
            new ModelMigrationRequest("openai", "some-model", "https://api.example.com/v1"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>The key prefix and a value each project-keyed settings writer sends: access set, watch enable, watch disable, watch concurrency, ingest scope add.</summary>
    public static TheoryData<string, string> Writers => new()
    {
        { "access.mode.project:", "ro" },
        { "watch.enabled.", "true" },
        { "watch.enabled.", "false" },
        { "watch.concurrency.", "4" },
        { "ingest.scope.", """["/repo"]""" }
    };

    [RetryTheory]
    [MemberData(nameof(Writers))]
    public async Task Put_UnknownId_Is409WritesNothing(string prefix, string value)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PutAsync(prefix + Unknown, value);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain($"Project '{Unknown}' is not registered");
        (await BankSettingAsync(prefix + Unknown)).ShouldBeNull();
        (await Registry.IsRegisteredAsync(Unknown, ct)).ShouldBeFalse();
    }

    [RetryTheory]
    [InlineData("registered")]
    [InlineData("row-holding")]
    public async Task Put_RegisteredOrRowHolding_Writes(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        if (kind == "registered")
        {
            await Registry.RegisterAsync(Unknown, null, ct);
        }
        else
        {
            await SeedEntryAsync(Unknown);
        }

        foreach (var (prefix, value) in Writers.Select(row => row.Data))
        {
            (await PutAsync(prefix + Unknown, value)).StatusCode.ShouldBe(HttpStatusCode.NoContent, prefix);
            (await BankSettingAsync(prefix + Unknown)).ShouldBe(value, prefix);
        }
    }

    [RetryTheory]
    [MemberData(nameof(Writers))]
    public async Task Put_DroppedId_Is409Retired(string prefix, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync(Retired, null, ct);
        ProjectIdAliasMap.ReplaceDefault(new ProjectIdAliasMap([], [], [Retired]));

        var response = await PutAsync(prefix + Retired.ToUpperInvariant(), value);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain($"Project '{Retired}' is retired");
        (await BankSettingAsync(prefix + Retired)).ShouldBeNull();
    }

    [RetryTheory]
    [MemberData(nameof(Writers))]
    public async Task Put_UppercaseBracedGuid_WritesCanonicalKey(string prefix, string value)
    {
        await Registry.RegisterAsync(Winner, null, TestContext.Current.CancellationToken);
        var braced = $"{{{Winner.ToUpperInvariant()}}}";

        (await PutAsync(prefix + braced, value)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await BankSettingAsync(prefix + Winner)).ShouldBe(value);
        (await BankSettingAsync(prefix + braced)).ShouldBeNull();
    }

    [RetryTheory]
    [MemberData(nameof(Writers))]
    public async Task Put_UnderAnAlias_WritesAndReadsTheWinnersKey(string prefix, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync(Winner, null, ct);
        ProjectIdAliasMap.ReplaceDefault(AliasMap());

        (await PutAsync(prefix + Loser, value)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await BankSettingAsync(prefix + Winner)).ShouldBe(value);
        (await BankSettingAsync(prefix + Loser)).ShouldBeNull();
        var read = await _client.GetAsync($"/settings?key={Uri.EscapeDataString(prefix + Loser)}", ct);
        (await read.Content.ReadFromJsonAsync<SettingValue>(ct)).ShouldNotBeNull().Value.ShouldBe(value);
    }

    /// <summary>
    ///     <c>ingest scope add</c> reads, adds and writes back. A CLI with an empty alias map sends the
    ///     loser's key both times, so the read has to resolve too, or the write replaces the winner's
    ///     list with the one new path.
    /// </summary>
    [RetryFact]
    public async Task ScopeAddUnderAlias_KeepsWinnersPaths()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync(Winner, null, ct);
        var winnerPath = Path.GetFullPath(Path.Combine(_dataRoot, "winner"));
        var addedPath = Path.GetFullPath(Path.Combine(_dataRoot, "added"));
        await BankStore.SetSettingAsync($"ingest.scope.{Winner}", IngestScopeList.ToJson([winnerPath]), ct);
        ProjectIdAliasMap.ReplaceDefault(AliasMap());
        using var http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        var cli = new ServerSettingsStore(http, Token, CliSettingsBackend.RequestDeadline);
        var loserKey = $"ingest.scope.{Loser}";

        var current = IngestScopeList.Parse(await cli.GetSettingAsync(loserKey, ct));
        await cli.SetSettingAsync(loserKey, IngestScopeList.ToJson(IngestScopeList.Add(current, addedPath)), ct);

        IngestScopeList.Parse(await BankSettingAsync($"ingest.scope.{Winner}"))
            .ShouldBe(new[] { winnerPath, addedPath }.Order(StringComparer.Ordinal));
        (await BankSettingAsync(loserKey)).ShouldBeNull();
    }

    [RetryFact]
    public async Task ScopeRemoveUnderAlias_RemovesLastWinnerPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync(Winner, null, ct);
        var path = Path.GetFullPath(Path.Combine(_dataRoot, "only-scope"));
        var winnerKey = $"ingest.scope.{Winner}";
        var loserKey = $"ingest.scope.{Loser}";
        await BankStore.SetSettingAsync(winnerKey, IngestScopeList.ToJson([path]), ct);
        ProjectIdAliasMap.ReplaceDefault(AliasMap());
        using var http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        var cli = new ServerSettingsStore(http, Token, CliSettingsBackend.RequestDeadline);

        var current = IngestScopeList.Parse(await cli.GetSettingAsync(loserKey, ct));
        var updated = IngestScopeList.Remove(current, path);
        updated.ShouldBeEmpty();
        await cli.DeleteSettingAsync(loserKey, ct);

        (await BankSettingAsync(winnerKey)).ShouldBeNull();
        (await BankSettingAsync(loserKey)).ShouldBeNull();
    }

    /// <summary>
    ///     <c>ingest scope remove</c> stays open under any id. Only <c>ingest.scope.</c> is exercised:
    ///     every bank open renames a legacy <c>watch.scope.</c> row to <c>ingest.scope.</c>.
    /// </summary>
    [RetryFact]
    public async Task Put_ScopeSubset_UnderUnknownId_Writes()
    {
        var key = $"ingest.scope.{Unknown}";
        await BankStore.SetSettingAsync(key, """["/a","/b"]""", TestContext.Current.CancellationToken);

        (await PutAsync(key, """["/a"]""")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await BankSettingAsync(key)).ShouldBe("""["/a"]""");
    }

    /// <summary>The removal exemption is a scope-list rule, checked before the retired refusal: a pure shrink writes under any id.</summary>
    [RetryTheory]
    [InlineData("registered")]
    [InlineData("unregistered")]
    [InlineData("retired")]
    public async Task Put_ScopeSubset_UnderAnyId_Writes(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Unknown;
        if (kind == "registered")
        {
            await Registry.RegisterAsync(Unknown, null, ct);
        }
        else if (kind == "retired")
        {
            id = Retired;
            await Registry.RegisterAsync(Retired, null, ct);
            ProjectIdAliasMap.ReplaceDefault(new ProjectIdAliasMap([], [], [Retired]));
        }

        var key = $"ingest.scope.{id}";
        await BankStore.SetSettingAsync(key, """["/a","/b"]""", ct);

        (await PutAsync(key, """["/a"]""")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await BankSettingAsync(key)).ShouldBe("""["/a"]""");
    }

    /// <summary>A write that is not a pure shrink under a retired id stays refused.</summary>
    [RetryFact]
    public async Task Put_ScopeSuperset_UnderRetiredId_Is409()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync(Retired, null, ct);
        ProjectIdAliasMap.ReplaceDefault(new ProjectIdAliasMap([], [], [Retired]));
        var key = $"ingest.scope.{Retired}";
        await BankStore.SetSettingAsync(key, """["/a"]""", ct);

        (await PutAsync(key, """["/a","/b"]""")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await BankSettingAsync(key)).ShouldBe("""["/a"]""");
    }

    [RetryFact]
    public async Task Put_ScopeSuperset_UnderUnknownId_Is409()
    {
        var key = $"ingest.scope.{Unknown}";
        await BankStore.SetSettingAsync(key, """["/a"]""", TestContext.Current.CancellationToken);

        (await PutAsync(key, """["/a","/b"]""")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await BankSettingAsync(key)).ShouldBe("""["/a"]""");
    }

    /// <summary>The removal exemption is a scope-list rule: a non-scope key under an unknown id is refused even when its value only shrinks.</summary>
    [RetryTheory]
    [InlineData("watch.enabled.")]
    [InlineData("watch.concurrency.")]
    [InlineData("access.mode.project:")]
    public async Task Put_WatchDisable_UnknownId_Is409(string prefix)
    {
        await BankStore.SetSettingAsync(prefix + Unknown, """["/a"]""", TestContext.Current.CancellationToken);

        (await PutAsync(prefix + Unknown, "[]")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await BankSettingAsync(prefix + Unknown)).ShouldBe("""["/a"]""");
    }

    /// <summary>The keys a <c>*</c> target writes are global, not a project's.</summary>
    [RetryTheory]
    [InlineData("access.mode.global", "ro")]
    [InlineData("watch.enabled.global", "true")]
    [InlineData("watch.concurrency.global", "4")]
    [InlineData("ingest.scope.global", """["/repo"]""")]
    public async Task Put_Wildcard_Writes(string key, string value)
    {
        (await PutAsync(key, value)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await BankSettingAsync(key)).ShouldBe(value);
    }

    [RetryTheory]
    [InlineData("access.mode.project:")]
    [InlineData("access.mode.project:  ")]
    [InlineData("ingest.scope.")]
    [InlineData("watch.enabled. ")]
    public async Task Put_BlankOwner_Is400(string key)
    {
        (await PutAsync(key, "ro")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await BankSettingAsync(key)).ShouldBeNull();
    }

    /// <summary>The blank-owner refusal echoes the key; the echo goes through ProjectIdText.</summary>
    [RetryFact]
    public async Task Put_BlankOwnerWithControlCharacter_Is400WithAPrintableEcho()
    {
        var response = await PutAsync("access.mode.project:\r", "ro");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("\\u000D");
        body.Any(char.IsControl).ShouldBeFalse(body);
    }

    /// <summary>A fresh bank is unmigrated, where a write-side guard would auto-register a raw-text id; the settings check never does.</summary>
    [RetryFact]
    public async Task Put_RefusedOnUnmigratedBank_RegistersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _app.Services.GetRequiredService<IProjectIdsMigrationGate>().IsMigratedAsync(ct)).ShouldBeFalse();

        var response = await PutAsync("access.mode.project:legacy-text", "ro");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Registry.IsRegisteredAsync("legacy-text", ct)).ShouldBeFalse();
    }

    private IProjectRegistry Registry => _app.Services.GetRequiredService<IProjectRegistry>();

    private ISettingsStore BankStore => _app.Services.GetRequiredService<ISettingsStore>();

    private Task<string?> BankSettingAsync(string key) => BankStore.GetSettingAsync(key, TestContext.Current.CancellationToken);

    private static ProjectIdAliasMap AliasMap() => new([new ProjectIdAliasEntry(Loser, Winner)], [Winner], []);

    private async Task SeedEntryAsync(string projectId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _app.Services.GetRequiredService<ISqliteConnectionFactory>().OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
            "VALUES ('legacy', 'legacy', 'legacy', 'seed.md', 's', 'project', @projectId, NULL, 1, 1, 'pending')",
            new { projectId }, cancellationToken: ct));
    }

    private Task<HttpResponseMessage> PutAsync(string key, string value) =>
        _client.PutAsJsonAsync("/settings", new SettingWrite(key, value), TestContext.Current.CancellationToken);
}
