using System.Net;
using System.Text.Json;

namespace YourProject.Mcp.Tests;

/// <summary>
/// Reusable mock HttpMessageHandler for testing MCP tools that call REST APIs.
/// Captures the last request's method, URI, and JSON body for assertions.
///
/// Usage:
///   var handler = new MockHttpHandler(HttpStatusCode.OK, """{"ok":true}""");
///   var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:7071") };
///   var tools = new MyTools(httpClient);
///   // ... call tool methods ...
///   Assert.Equal(HttpMethod.Post, handler.LastMethod);
///   Assert.Equal("/api/expected/path", handler.LastRequestUri!.AbsolutePath);
///   Assert.Equal("value", handler.LastRequestBody!.RootElement.GetProperty("key").GetString());
/// </summary>
internal sealed class MockHttpHandler(HttpStatusCode statusCode, string responseJson) : HttpMessageHandler
{
    public HttpMethod? LastMethod { get; private set; }
    public Uri? LastRequestUri { get; private set; }
    public JsonDocument? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastMethod = request.Method;
        LastRequestUri = request.RequestUri;

        if (request.Content is not null)
        {
            var bodyString = await request.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrEmpty(bodyString))
            {
                LastRequestBody = JsonDocument.Parse(bodyString);
            }
        }

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
