## 10. Testing pattern

```csharp
using System.Net;
using System.Text.Json;

public class MyToolsTests
{
    private static (MyTools tools, MockHttpHandler handler) CreateTools(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseJson = null)
    {
        var handler = new MockHttpHandler(statusCode, responseJson ?? """{"ok":true}""");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:7071")
        };
        return (new MyTools(httpClient), handler);
    }

    [Fact]
    public async Task DoSomething_PostsToCorrectEndpoint()
    {
        var (tools, handler) = CreateTools(responseJson: """{"result":"done"}""");
        var result = await tools.DoSomething("res-1", 100m);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/api/resources/res-1/action", handler.LastRequestUri!.AbsolutePath);
        Assert.Contains("done", result);
    }

    [Fact]
    public async Task DoSomething_SendsCorrectBody()
    {
        var (tools, handler) = CreateTools();
        await tools.DoSomething("res-1", 200m, "Yearly");
        var body = handler.LastRequestBody!;
        Assert.Equal(200, body.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal("Yearly", body.RootElement.GetProperty("period").GetString());
    }

    [Fact]
    public async Task DoSomething_ThrowsOnNonSuccessStatusCode()
    {
        var (tools, _) = CreateTools(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<HttpRequestException>(() => tools.DoSomething("x", 1m));
    }

    // MockHttpMessageHandler that captures request details for assertions
    private sealed class MockHttpHandler(HttpStatusCode statusCode, string responseJson)
        : HttpMessageHandler
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
                    LastRequestBody = JsonDocument.Parse(bodyString);
            }
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
```

### What to test per tool

1. **Endpoint routing**: correct HTTP method + URL path
2. **Request body**: all parameters serialized correctly with expected defaults
3. **Response handling**: tool returns the API's JSON string
4. **Error propagation**: `EnsureSuccessStatusCode()` throws on non-2xx
