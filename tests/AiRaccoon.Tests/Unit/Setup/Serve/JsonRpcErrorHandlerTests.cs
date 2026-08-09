using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiRaccoon.Setup.Serve;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     What the proxy's error-recovery handler may and may not touch (ADR-0020): a JSON-RPC error
///     behind an error status is handed on for the SDK to convert; anything else is left alone.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class JsonRpcErrorHandlerTests
{
    private const string MethodNotFound =
        """{"error":{"code":-32601,"message":"Method 'x/unknown' is not available."},"id":"1","jsonrpc":"2.0"}""";

    [Fact]
    public async Task EventStreamErrorBody_IsHandedOnForConversion()
    {
        var response = await SendAsync(HttpStatusCode.NotFound, "text/event-stream",
            $"event: message\ndata: {MethodNotFound}\n\n");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("-32601");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
    }

    [Fact]
    public async Task JsonErrorBody_IsHandedOnForConversion()
    {
        var response = await SendAsync(HttpStatusCode.BadRequest, "application/json", MethodNotFound);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    ///     The reason there is no status-to-code table: a 404 from a wrong path is a real 404, and a
    ///     table keyed on the status would have turned it into MethodNotFound.
    /// </summary>
    [Fact]
    public async Task EndpointNotFound_StaysAFailure()
    {
        var response = await SendAsync(HttpStatusCode.NotFound, "text/html", "<html>no such route</html>");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ErrorStatusWithASuccessfulJsonRpcBody_StaysAFailure()
    {
        // A result is not an error: only a JSON-RPC error object earns the rewrite.
        var response = await SendAsync(HttpStatusCode.InternalServerError, "application/json",
            """{"result":{},"id":"1","jsonrpc":"2.0"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    /// <summary>
    ///     The gate answers 401 with a JSON-RPC error carrying a null id (McpTokenGate). Rewritten to
    ///     200 it correlates with no pending request, so the SDK drops it and `initialize` hangs to its
    ///     timeout instead of reporting the token file.
    /// </summary>
    [Fact]
    public async Task GateRefusal_StaysAFailure()
    {
        var response = await SendAsync(HttpStatusCode.Unauthorized, "application/json",
            """
            {"jsonrpc":"2.0","id":null,"error":{"code":-32001,"message":"ai-raccoon: /mcp needs the X-AiRaccoon-Token header; the token is in /x/mcp-token"}}
            """);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    ///     The general rule the gate's 401 is one case of. Body measured from a real `serve`: a
    ///     malformed POST answers 400 with id null, and nothing correlates with that either.
    /// </summary>
    [Fact]
    public async Task ErrorBodyWithNoCorrelatingId_StaysAFailure()
    {
        var response = await SendAsync(HttpStatusCode.BadRequest, "application/json",
            """
            {"error":{"code":-32600,"message":"Bad Request: The POST body did not contain a valid JSON-RPC message."},"id":null,"jsonrpc":"2.0"}
            """);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SuccessfulResponse_IsUntouched()
    {
        var response = await SendAsync(HttpStatusCode.OK, "text/event-stream", $"event: message\ndata: {MethodNotFound}\n\n");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpStatusCode status, string mediaType, string body)
    {
        var handler = new JsonRpcErrorHandler { InnerHandler = new CannedHandler(status, mediaType, body) };
        using var client = new HttpClient(handler);
        return await client.GetAsync(new Uri("http://127.0.0.1/mcp"), TestContext.Current.CancellationToken);
    }

    private sealed class CannedHandler(HttpStatusCode status, string mediaType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue(mediaType))
            });
    }
}
