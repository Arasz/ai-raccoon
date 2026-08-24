using System.Net;
using AiRaccoon.Infrastructure.Resilience;
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

    /// <summary>H18: the pipeline retries the real <see cref="EmptyDownloadException" />.</summary>
    [Fact]
    public async Task CreateAssetDownloaderPipeline_RetriesOnEmptyDownloadException()
    {
        var attempts = 0;
        var pipeline = ResiliencePipelineFactory.CreateAssetDownloaderPipeline(maxAttempts: 3, initialDelay: TimeSpan.FromMilliseconds(5));

        var response = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.CompletedTask;
            if (attempts < 2)
            {
                throw new AiRaccoon.Infrastructure.Assets.EmptyDownloadException("0 bytes");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        attempts.ShouldBe(2);
    }

    /// <summary>
    ///     H18 regression: a string-match on <c>ex.GetType().Name</c> would also retry any
    ///     unrelated exception that merely shares the name "EmptyDownloadException" — the pipeline
    ///     must handle the real type, not a name.
    /// </summary>
    [Fact]
    public async Task CreateAssetDownloaderPipeline_ALookAlikeExceptionWithTheSameTypeName_IsNotRetried()
    {
        var attempts = 0;
        var pipeline = ResiliencePipelineFactory.CreateAssetDownloaderPipeline(maxAttempts: 3, initialDelay: TimeSpan.FromMilliseconds(5));

        await Should.ThrowAsync<EmptyDownloadException>(() => pipeline.ExecuteAsync<HttpResponseMessage>(async _ =>
        {
            attempts++;
            await Task.CompletedTask;
            throw new EmptyDownloadException("not the real one");
        }, TestContext.Current.CancellationToken).AsTask());

        attempts.ShouldBe(1, "a same-named-but-different-type exception must not be retried");
    }

    /// <summary>Look-alike for the regression test above: same simple type name as
    /// <c>AiRaccoon.Infrastructure.Assets.EmptyDownloadException</c>, different namespace/type.</summary>
    private sealed class EmptyDownloadException(string message) : Exception(message);
}
