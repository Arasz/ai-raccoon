using System.Net;
using AiRaccoon.Settings;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>A CLI store whose settings writes reach a server that refuses the project with a 409 carrying <see cref="Reason" />; reads find nothing.</summary>
internal sealed class ProjectRefusingStore : FakeMemoryStore
{
    public const string Reason = "ai-raccoon: settings write refused: Project 'acme' is not registered.";

    private readonly ServerSettingsStore _server = new(
        new HttpClient(new RefusingHandler()) { BaseAddress = new Uri("http://127.0.0.1:1/") },
        "test-token", CliSettingsBackend.RequestDeadline);

    public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
        _server.SetSettingAsync(key, value, cancellationToken);

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(Reason) });
    }
}
