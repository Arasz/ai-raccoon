using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     WP7 §5.3: the CLI's half of the settings transport. Same <see cref="Core.Memory.ISettingsStore" />
///     surface the bank-backed store implements, so a subsystem keeps one implementation and only
///     the transport under it changes.
///     <para />
///     Not registered in DI yet. Wiring it into the CLI composition root is the follow-up; this
///     lane proves the transport works and how it fails.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ServerSettingsStoreTests : IAsyncLifetime
{
    private const string Token = "server-settings-store-token";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-server-settings-store");
    private WebApplication _app = null!;
    private ServerSettingsStore _store = null!;
    private Uri _baseAddress = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, options) { McpToken = Token });
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _baseAddress = new Uri(_app.Urls.First());
        _store = NewStore(_baseAddress, Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    private static ServerSettingsStore NewStore(Uri baseAddress, string token) =>
        new(new HttpClient { BaseAddress = baseAddress }, token);

    [RetryFact]
    public async Task SetThenGet_RoundTrips()
    {
        await _store.SetSettingAsync("sweep.threshold", "0.7", TestContext.Current.CancellationToken);

        (await _store.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken)).ShouldBe("0.7");
    }

    /// <summary>Absent reads as null, exactly as the bank-backed store reports it.</summary>
    [RetryFact]
    public async Task GetSetting_ForAnAbsentKey_IsNull()
    {
        (await _store.GetSettingAsync("never.written", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [RetryFact]
    public async Task GetSettingsByPrefix_MatchesTheExactPrefixOnly()
    {
        await _store.SetSettingAsync("queryGuard.enabled.global", "false", TestContext.Current.CancellationToken);
        await _store.SetSettingAsync("queryGuardX.other", "x", TestContext.Current.CancellationToken);

        var rows = await _store.GetSettingsByPrefixAsync("queryGuard.", TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(1);
        rows["queryGuard.enabled.global"].ShouldBe("false");
    }

    [RetryFact]
    public async Task GetSettingsByPrefix_WithNoMatch_IsEmpty_NotAThrow()
    {
        (await _store.GetSettingsByPrefixAsync("nothing.", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [RetryFact]
    public async Task DeleteSetting_RemovesTheRow()
    {
        await _store.SetSettingAsync("noise.enabled.global", "false", TestContext.Current.CancellationToken);

        await _store.DeleteSettingAsync("noise.enabled.global", TestContext.Current.CancellationToken);

        (await _store.GetSettingAsync("noise.enabled.global", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>A key with characters that must survive the query string rather than truncate it.</summary>
    [RetryFact]
    public async Task KeysAreEscaped_SoAKeyCannotForgeAQueryString()
    {
        await _store.SetSettingAsync("access.mode.project:a&prefix=b", "ro", TestContext.Current.CancellationToken);

        (await _store.GetSettingAsync("access.mode.project:a&prefix=b", TestContext.Current.CancellationToken))
            .ShouldBe("ro");
    }

    /// <summary>WP7-T7's server-refused row: the wrong token must say so, not read as absent.</summary>
    [RetryFact]
    public async Task AWrongToken_ThrowsRefused_RatherThanReportingAbsent()
    {
        var refused = NewStore(_baseAddress, "not-the-token");

        var error = await Should.ThrowAsync<SettingsServerRefusedException>(
            () => refused.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("refused");
    }

    [RetryFact]
    public async Task AWrongToken_OnAWrite_ThrowsRefused()
    {
        var refused = NewStore(_baseAddress, "not-the-token");

        await Should.ThrowAsync<SettingsServerRefusedException>(
            () => refused.SetSettingAsync("sweep.threshold", "0.1", TestContext.Current.CancellationToken));
    }

    /// <summary>WP7-T7's server-unreachable row.</summary>
    [RetryFact]
    public async Task AnUnreachableServer_ThrowsUnavailable_NamingTheAddress()
    {
        var unreachable = NewStore(new Uri("http://127.0.0.1:1/"), Token);

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(
            () => unreachable.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("127.0.0.1:1");
    }

    /// <summary>A write must never look like it landed when the server never answered.</summary>
    [RetryFact]
    public async Task AnUnreachableServer_OnAWrite_ThrowsUnavailable()
    {
        var unreachable = NewStore(new Uri("http://127.0.0.1:1/"), Token);

        await Should.ThrowAsync<SettingsServerUnavailableException>(
            () => unreachable.SetSettingAsync("sweep.threshold", "0.1", TestContext.Current.CancellationToken));
    }

    /// <summary>ADR-0075 amendment: `noise entries` reaches noise_entries entirely through the server.</summary>
    [RetryFact]
    public async Task SummarizeAsync_ReturnsTheLiveSummary()
    {
        var summary = await _store.SummarizeAsync(TestContext.Current.CancellationToken);

        summary.TotalCount.ShouldBe(0);
    }

    /// <summary>ADR-0075 amendment: `watch registered` reaches the live registration list entirely through the server.</summary>
    [RetryFact]
    public async Task ListWatchesAsync_ReturnsTheLiveList()
    {
        var watches = await _store.ListWatchesAsync(TestContext.Current.CancellationToken);

        watches.ShouldBeEmpty();
    }
}
