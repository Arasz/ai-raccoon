using System.Diagnostics;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Hosting;
using AiRaccoon.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ObservabilityCollection.Name)]
public sealed class McpRequestTelemetryMiddlewareTests
{
    [Fact]
    public async Task PostMcp_RecordsDispatchDelayAndRequestDurationThroughTheResponse()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var metrics = new ToolCallMetrics();
        var recorder = new RecordingRecorder();
        using var dispatchCollector = new MetricCollector<double>(metrics.Meter, "ai_raccoon.mcp.dispatch.delay");
        using var requestCollector = new MetricCollector<double>(metrics.Meter, "ai_raccoon.mcp.request.duration");
        var responseFeature = new RecordingResponseFeature();
        var middleware = new McpRequestTelemetryMiddleware(async requestContext =>
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(50));
            using var call = new ToolExecutionActivity(metrics, "query", "acme", recorder: recorder,
                timeProvider: timeProvider, requestContext: requestContext);
            call.RecordInvocation();
            timeProvider.Advance(TimeSpan.FromMilliseconds(75));
            await Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        context.Features.Set<IHttpResponseFeature>(responseFeature);

        await middleware.InvokeAsync(context, timeProvider, metrics, recorder);

        dispatchCollector.GetMeasurementSnapshot().ShouldHaveSingleItem().Value.ShouldBe(0.05, 0.001);
        requestCollector.GetMeasurementSnapshot().ShouldBeEmpty();
        recorder.Recorded.ShouldNotContain(m => m.Name == "mcp.request.duration_ms");

        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        await responseFeature.CompleteResponseAsync();

        requestCollector.GetMeasurementSnapshot().ShouldHaveSingleItem().Value.ShouldBe(0.375, 0.001);
        recorder.Recorded.ShouldContain(m => m.Name == "mcp.dispatch.delay_ms" && m.ProjectId == "acme" && m.Value == 50);
        recorder.Recorded.ShouldContain(m => m.Name == "mcp.request.duration_ms" &&
            m.ProjectId == "__self_metrics__" && m.Value == 375);
    }

    [Fact]
    public async Task PostMcp_RecordsDurationWhenTheRequestIsAborted()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var metrics = new ToolCallMetrics();
        var recorder = new RecordingRecorder();
        using var requestCollector = new MetricCollector<double>(metrics.Meter, "ai_raccoon.mcp.request.duration");
        using var aborted = new CancellationTokenSource();
        var middleware = new McpRequestTelemetryMiddleware(_ =>
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(50));
            aborted.Cancel();
            throw new OperationCanceledException(aborted.Token);
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        context.RequestAborted = aborted.Token;

        await Should.ThrowAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(context, timeProvider, metrics, recorder));

        requestCollector.GetMeasurementSnapshot().ShouldHaveSingleItem().Value.ShouldBe(0.05, 0.001);
        recorder.Recorded.ShouldContain(m => m.Name == "mcp.request.duration_ms" &&
            m.ProjectId == "__self_metrics__" && m.Value == 50);
    }

    [Fact]
    public async Task PostMcp_AbortedAndCompleted_RecordsDurationOnce()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var metrics = new ToolCallMetrics();
        var recorder = new RecordingRecorder();
        using var requestCollector = new MetricCollector<double>(metrics.Meter, "ai_raccoon.mcp.request.duration");
        using var aborted = new CancellationTokenSource();
        var responseFeature = new RecordingResponseFeature();
        var middleware = new McpRequestTelemetryMiddleware(_ =>
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(50));
            aborted.Cancel();
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        context.RequestAborted = aborted.Token;
        context.Features.Set<IHttpResponseFeature>(responseFeature);

        await middleware.InvokeAsync(context, timeProvider, metrics, recorder);
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await responseFeature.CompleteResponseAsync();

        requestCollector.GetMeasurementSnapshot().ShouldHaveSingleItem().Value.ShouldBe(0.05, 0.001);
        recorder.Recorded.Count(m => m.Name == "mcp.request.duration_ms").ShouldBe(1);
    }

    [Fact]
    public async Task PostMcp_LeavesTheCallersActivityRunning()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var metrics = new ToolCallMetrics();
        var responseFeature = new RecordingResponseFeature();
        var middleware = new McpRequestTelemetryMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        using var hostingActivity = new Activity("hosting").Start();

        await middleware.InvokeAsync(context, timeProvider, metrics, new RecordingRecorder());
        await responseFeature.CompleteResponseAsync();

        hostingActivity.IsStopped.ShouldBeFalse();
    }

    private sealed class RecordingRecorder : IMeasurementRecorder
    {
        public List<Measurement> Recorded { get; } = [];
        public void Record(Measurement measurement) => Recorded.Add(measurement);
    }

    private sealed class RecordingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onCompleted = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; set; }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state) => _onCompleted.Add((callback, state));

        public async Task CompleteResponseAsync()
        {
            foreach (var (callback, state) in _onCompleted.AsEnumerable().Reverse())
            {
                await callback(state);
            }
        }
    }
}
