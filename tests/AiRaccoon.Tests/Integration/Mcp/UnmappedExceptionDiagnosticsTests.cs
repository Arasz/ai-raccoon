using AiRaccoon.Hosting.Common;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Setup;
using AiRaccoon.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     An exception `ToolRefusals` does not map used to escape the filter, so the SDK replaced it
///     with "An error occurred invoking '&lt;tool&gt;'." — eleven words carrying neither the type nor
///     the message. An agent, an operator and a CI log all got the same eleven words, which is what
///     made this class's intermittent reds unreadable (WP19, docs/adr/0061).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class UnmappedExceptionDiagnosticsTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("unmapped-exception-diagnostics");

    private IAsyncDisposable? _envGate;

    /// <summary>
    ///     Holds the env gate as a reader: this class opens a bank through the real host, so an
    ///     encryption test's window would make it open a plain bank with a key (docs/adr/0066).
    /// </summary>
    public async ValueTask InitializeAsync() =>
        _envGate = await TestData.HoldEnvGateAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        if (_envGate is not null)
        {
            await _envGate.DisposeAsync();
        }
    }

    /// <summary>The type name is the minimum a caller needs to tell one failure from another.</summary>
    [Fact]
    public void UnexpectedText_NamesTheExceptionType() =>
        ToolRefusals.UnexpectedText(new InvalidOperationException("detail the client must not see"))
            .ShouldBe("unexpected-error: InvalidOperationException");

    /// <summary>
    ///     The message stays server-side. A refusal's text is chosen for the caller; an unexpected
    ///     failure's is not, and may carry a path or a SQL fragment.
    /// </summary>
    [Fact]
    public void UnexpectedText_DoesNotCarryTheMessage() =>
        ToolRefusals.UnexpectedText(new InvalidOperationException("bank at /Users/someone/secret.db is locked"))
            .ShouldNotContain("secret.db");

    /// <summary>
    ///     End to end against a real server, with the bank replaced by garbage so the store throws
    ///     something no refusal maps — the shape WP19 predicted was behind the intermittent reds.
    ///     Asserts the *shape*, not a pinned exception type: which type surfaces depends on where the
    ///     open fails, and pinning it would make this test about SQLite rather than about diagnosability.
    /// </summary>
    [Fact]
    public async Task UnmappedException_ReachesTheClientWithItsTypeAndIsLoggedAtError()
    {
        var bank = Path.Combine(_dataRoot, "memory.db");
        await File.WriteAllTextAsync(bank, "this is not a SQLite database",
            TestContext.Current.CancellationToken);

        var fakeLogs = new FakeLoggerProvider();
        var (port, host) = await LoopbackPort.BindWithRetryAsync(async candidate =>
        {
            var started = McpServerSetup.CreateServerHost(
                new ServerConfig(candidate, McpTransport.Http, TestData.CreateInfrastructureOptions(_dataRoot)));
            started.Services.GetRequiredService<ILoggerFactory>().AddProvider(fakeLogs);
            await started.StartAsync(TestContext.Current.CancellationToken);
            return (candidate, started);
        });

        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "unmapped-exception-test",
                    Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                httpClient, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, true);
            await using var client = await McpClient.CreateAsync(transport,
                cancellationToken: TestContext.Current.CancellationToken);

            var result = await client.CallToolAsync("memory_stats",
                new Dictionary<string, object?> { ["projectId"] = "anything" },
                cancellationToken: TestContext.Current.CancellationToken);

            result.IsError.ShouldBe(true);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

            text.ShouldStartWith("unexpected-error: ",
                customMessage: $"the caller must learn what failed, not just that something did; got: {text}");
            text.Length.ShouldBeGreaterThan("unexpected-error: ".Length,
                "the prefix alone is no better than the eleven words it replaced");

            var errors = fakeLogs.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Error).ToList();
            errors.ShouldNotBeEmpty(
                "catching the exception must not cost the Error record the SDK used to produce");
            errors.ShouldContain(r => r.Exception != null,
                "the Error record carries the exception, so the server side keeps the full detail");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
