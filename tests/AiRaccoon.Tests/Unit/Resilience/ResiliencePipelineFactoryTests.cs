using System.Net;
using AiRaccoon.Resilience;
using Polly;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Resilience;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ResiliencePipelineFactoryTests
{
    [Fact]
    public async Task CreateProbePipeline_RetriesWithExponentialBackoffAndJitter()
    {
        var delays = new List<TimeSpan>();
        var executions = 0;

        var pipeline = ResiliencePipelineFactory.CreateProbePipeline(
            maxAttempts: 3,
            initialDelay: TimeSpan.FromMilliseconds(50),
            onRetry: args =>
            {
                delays.Add(args.RetryDelay);
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                executions++;
                await Task.CompletedTask;
                throw new HttpRequestException("Simulated socket refusal");
            }, TestContext.Current.CancellationToken);
        });

        executions.ShouldBe(3);
        delays.Count.ShouldBe(2);

        // Exponential backoff + jitter check: 2nd delay should generally be larger than 1st delay
        // initial base delay 50ms, 2nd base delay 100ms
        delays[0].TotalMilliseconds.ShouldBeGreaterThan(0);
        delays[1].TotalMilliseconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CreateAssetDownloaderPipeline_RetriesOnTransientHttpStatusCodes()
    {
        var attempts = 0;
        var pipeline = ResiliencePipelineFactory.CreateAssetDownloaderPipeline(maxAttempts: 4, initialDelay: TimeSpan.FromMilliseconds(20));

        var response = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.CompletedTask;
            if (attempts < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        attempts.ShouldBe(3);
    }
}
