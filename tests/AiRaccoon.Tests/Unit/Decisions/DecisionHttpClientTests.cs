using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Decisions;
using AiRaccoon.Decisions.Client;
using AiRaccoon.Settings;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Decisions;

/// <summary>What the CLI's decision client reports when an answer breaks off or a refusal body never ends.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DecisionHttpClientTests
{
    private const string Project = "0b7c2b0e-6a8e-4f7e-9d1a-2f3c4d5e6f70";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegisterTask_AnswerEndingEarly_SaysTheOutcomeIsUnknownAndHowToReplay()
    {
        var client = Client(HttpStatusCode.OK, new BrokenStream("{\"data\":"u8.ToArray()));

        var ex = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            client.RegisterTaskAsync(Project, new RegisterTaskRequest("t1", "s-1", "r-1", "Title"), Ct));

        ex.Message.ShouldNotContain("nothing was published");
        ex.Message.ShouldContain("outcome is unknown");
        ex.Message.ShouldContain("--session-id s-1 --request-id r-1");
    }

    [Fact]
    public async Task Record_AnswerEndingEarly_SaysTheOutcomeIsUnknownAndHowToReplay()
    {
        var client = Client(HttpStatusCode.OK, new BrokenStream("{\"data\":"u8.ToArray()));
        var body = new JsonObject { ["taskId"] = "t1", ["sessionId"] = "s-2", ["requestId"] = "r-2" };

        var ex = await Should.ThrowAsync<SettingsServerUnavailableException>(() => client.RecordAsync(Project, body, Ct));

        ex.Message.ShouldContain("outcome is unknown");
        ex.Message.ShouldContain("--session-id s-2 --request-id r-2");
    }

    [Fact]
    public async Task Export_AnswerEndingEarly_SaysNothingWasPublished()
    {
        var client = Client(HttpStatusCode.OK, new BrokenStream("{\"data\":"u8.ToArray()));

        var ex = await Should.ThrowAsync<SettingsServerUnavailableException>(() => client.ExportAsync(Project, null, Ct));

        ex.Message.ShouldContain("nothing was published");
        ex.Message.ShouldNotContain("outcome is unknown");
    }

    [Fact]
    public async Task Refusal_BodyThatNeverEnds_IsReportedAsTheRefusalWithTheTextReceived()
    {
        var stalled = new StallingStream("""{"kind":"decision-not-found","detail":"Decision was not"""u8.ToArray());
        var client = Client(HttpStatusCode.NotFound, stalled, refusalBodyDeadline: TimeSpan.FromMilliseconds(200));

        var ex = await Should.ThrowAsync<DecisionRefusedException>(() => client.ExportAsync(Project, null, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));

        ex.Status.ShouldBe(404);
        ex.Kind.ShouldBe("http-404");
        ex.Message.ShouldContain("Decision was not");
    }

    [Fact]
    public async Task Refusal_BodyThatEnds_IsReadInFullAndParsed()
    {
        var body = new ChunkedStream("""{"kind":"decision-not-found","detail":"Decision was not found."}"""u8.ToArray(), 7);
        var client = Client(HttpStatusCode.NotFound, body, refusalBodyDeadline: TimeSpan.FromSeconds(30));

        var ex = await Should.ThrowAsync<DecisionRefusedException>(() => client.GetAsync(Project, "d1", false, Ct));

        ex.Kind.ShouldBe(DecisionErrorKinds.NotFound);
        ex.Message.ShouldContain("Decision was not found.");
    }

    private static DecisionHttpClient Client(HttpStatusCode status, Stream body, TimeSpan? refusalBodyDeadline = null)
    {
        var http = new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("http://127.0.0.1:1/") };
        return refusalBodyDeadline is { } limit
            ? new DecisionHttpClient(http, TimeSpan.FromSeconds(30), limit)
            : new DecisionHttpClient(http, TimeSpan.FromSeconds(30));
    }

    private sealed class StubHandler(HttpStatusCode status, Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StreamContent(body) });
    }

    /// <summary>Yields its bytes, then fails as a reset connection would.</summary>
    private sealed class BrokenStream(byte[] head) : ChunkedStream(head, head.Length)
    {
        protected override ValueTask<int> AfterEndAsync(CancellationToken cancellationToken) =>
            throw new IOException("the connection was reset");
    }

    /// <summary>Yields its bytes, then waits for more that never come.</summary>
    private sealed class StallingStream(byte[] head) : ChunkedStream(head, head.Length)
    {
        protected override async ValueTask<int> AfterEndAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    /// <summary>Yields its bytes a few at a time, then ends.</summary>
    private class ChunkedStream(byte[] content, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= content.Length)
            {
                return AfterEndAsync(cancellationToken);
            }

            var count = Math.Min(Math.Min(chunk, buffer.Length), content.Length - _position);
            content.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return ValueTask.FromResult(count);
        }

        protected virtual ValueTask<int> AfterEndAsync(CancellationToken cancellationToken) => ValueTask.FromResult(0);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
