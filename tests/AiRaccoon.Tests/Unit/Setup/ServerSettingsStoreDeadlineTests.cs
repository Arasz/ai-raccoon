using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Settings;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     A settings call that outlives its deadline reads as "no server answered", but a repair report
///     scans the whole bank server-side and has no deadline: only the caller's token ends it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ServerSettingsStoreDeadlineTests
{
    private const string Token = "deadline-token";
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SlowerThanTheDeadline = TimeSpan.FromMilliseconds(150);

    [Theory]
    [InlineData(RepairKinds.ChunkIndex)]
    [InlineData(RepairKinds.Reingest)]
    [InlineData(RepairKinds.ProjectIds)]
    public async Task RepairReport_SlowerThanTheRequestDeadline_StillSucceeds(string kind)
    {
        var store = NewStore(new DelayingHandler(SlowerThanTheDeadline), ShortDeadline);

        await ReportAsync(store, kind, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NewStore_OnAClientThatHasAlreadySent_StillAnswers()
    {
        var client = new HttpClient(new DelayingHandler(TimeSpan.Zero)) { BaseAddress = new Uri("http://127.0.0.1:1/mcp") };
        var first = new ServerSettingsStore(client, Token, ShortDeadline);
        await first.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken);

        var second = new ServerSettingsStore(client, Token, ShortDeadline);

        (await second.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void CliClient_HasNoClientTimeout_SoOnlyTheRequestDeadlineBoundsACall()
    {
        using var client = CliSettingsBackend.CreateClient("http://127.0.0.1:1/mcp");

        client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
        client.BaseAddress.ShouldBe(new Uri("http://127.0.0.1:1/mcp"));
    }

    [Fact]
    public async Task GetSetting_SlowerThanTheRequestDeadline_ThrowsUnavailable()
    {
        var store = NewStore(new DelayingHandler(SlowerThanTheDeadline), ShortDeadline);

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(
            () => store.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken));

        error.Code.ShouldBe(ErrorCode.Reach.StoppedAnswering);
    }

    [Fact]
    public async Task GetSetting_WithinTheRequestDeadline_Answers()
    {
        var store = NewStore(new DelayingHandler(TimeSpan.Zero), TimeSpan.FromSeconds(30));

        (await store.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ChunkIndexReport_CancelledByTheCaller_ThrowsCancellation_NotUnavailable()
    {
        var store = NewStore(new DelayingHandler(TimeSpan.FromSeconds(30)), ShortDeadline);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var error = await Should.ThrowAsync<OperationCanceledException>(() => store.ReportChunkIndexAsync(caller.Token));

        error.ShouldNotBeOfType<SettingsServerUnavailableException>();
    }

    [Fact]
    public async Task GetSetting_CancelledByTheCaller_ThrowsCancellation_NotUnavailable()
    {
        var store = NewStore(new DelayingHandler(TimeSpan.FromSeconds(30)), TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => store.GetSettingAsync("sweep.threshold", caller.Token));
    }

    private static ServerSettingsStore NewStore(HttpMessageHandler handler, TimeSpan requestDeadline) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/mcp") }, Token, requestDeadline);

    private static Task ReportAsync(ServerSettingsStore store, string kind, CancellationToken cancellationToken) =>
        kind switch
        {
            RepairKinds.ChunkIndex => store.ReportChunkIndexAsync(cancellationToken),
            RepairKinds.Reingest => store.ReportReingestAsync(cancellationToken),
            RepairKinds.ProjectIds => store.ReportProjectIdsAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    /// <summary>Answers every request after <paramref name="delay" />: 404 for a setting, a canned body for a repair report.</summary>
    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            var query = request.RequestUri!.Query;
            object? body = query.Contains(RepairKinds.ChunkIndex, StringComparison.Ordinal) ? new ChunkIndexRepairReport(7, 0, 0, 0)
                : query.Contains(RepairKinds.Reingest, StringComparison.Ordinal) ? new ReingestRepairReport(0, 0, 0)
                : query.Contains(RepairKinds.ProjectIds, StringComparison.Ordinal) ? new ProjectIdCensusReport([], 0, 0, 0, 0, [])
                : null;
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) };
        }
    }
}
