using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Resilience;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ServerProbeResilienceTests
{
    [Fact]
    public async Task RespondsAsync_WhenFirstAttemptFailsWithHttpRequestException_RetriesAndSucceeds()
    {
        var attempts = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("Connection refused (127.0.0.1:7721)", new SocketException((int)SocketError.ConnectionRefused));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\": \"2.0\", \"result\": {}}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        using var client = new HttpClient(handler);
        var pipeline = ResiliencePipelineFactory.CreateProbePipeline();
        var probe = new ServerProbe(client, pipeline);

        var result = await probe.RespondsAsync(7721, CancellationToken.None);

        result.ShouldBeTrue();
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task RespondsAsync_WhenAllAttemptsFail_ReturnsFalseWithoutThrowing()
    {
        var attempts = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            attempts++;
            throw new HttpRequestException("Connection refused (127.0.0.1:7721)", new SocketException((int)SocketError.ConnectionRefused));
        });

        using var client = new HttpClient(handler);
        var pipeline = ResiliencePipelineFactory.CreateProbePipeline(maxAttempts: 3);
        var probe = new ServerProbe(client, pipeline);

        var result = await probe.RespondsAsync(7721, CancellationToken.None);

        result.ShouldBeFalse();
        attempts.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task RespondsAsync_WhenServerReturns503ThenOk_RetriesAndSucceeds()
    {
        var attempts = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\": \"2.0\"}", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var pipeline = ResiliencePipelineFactory.CreateProbePipeline();
        var probe = new ServerProbe(client, pipeline);

        var result = await probe.RespondsAsync(7721, CancellationToken.None);

        result.ShouldBeTrue();
        attempts.ShouldBe(2);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
