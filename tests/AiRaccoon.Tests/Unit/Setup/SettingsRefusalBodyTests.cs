using System.Net;
using AiRaccoon.Core.Memory;
using AiRaccoon.Settings;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     The settings client's error path: a 4xx body is the server's refusal, and it names what the
///     operator has to do ("repair that owner"). <c>EnsureSuccessStatusCode()</c> threw the body
///     away, so the CLI answered "400 (Bad Request)" — the status without the sentence that says
///     which owner to repair.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SettingsRefusalBodyTests
{
    private const string Refusal =
        "shared-project-conflict: legacy private data or registration uses 'shared'; repair that owner before using the reserved target. No data was published.";

    [Fact]
    public async Task ARefusedWrite_CarriesTheServersRefusal()
    {
        var store = new ServerSettingsStore(
            new HttpClient(new RefusingHandler(Refusal)) { BaseAddress = new Uri("http://127.0.0.1:1/") },
            "test-token", CliSettingsBackend.RequestDeadline);

        var error = await Should.ThrowAsync<HttpRequestException>(() =>
            store.SetSettingAsync("ingest.scope.shared", "[\"/machine\"]", TestContext.Current.CancellationToken));

        error.Message.ShouldContain(Refusal);
        error.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            "the status must stay on the exception - the CLI maps it to the exit code that names the case");
    }

    /// <summary>
    ///     The 5xx path keeps its own message: a server fault is not a bad argument, and the body
    ///     of a fault does not say what to do.
    /// </summary>
    [Fact]
    public async Task AServerFault_KeepsItsOwnMessage()
    {
        var store = new ServerSettingsStore(
            new HttpClient(new FaultingHandler()) { BaseAddress = new Uri("http://127.0.0.1:1/") },
            "test-token", CliSettingsBackend.RequestDeadline);

        var error = await Should.ThrowAsync<SettingsServerErrorException>(() =>
            store.SetSettingAsync("ingest.scope.shared", "[\"/machine\"]", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("server-side fault");
    }

    private sealed class RefusingHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) });
    }

    private sealed class FaultingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("something broke inside")
            });
    }
}
