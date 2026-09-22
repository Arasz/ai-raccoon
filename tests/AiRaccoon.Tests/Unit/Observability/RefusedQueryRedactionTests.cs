using System.Diagnostics;
using AiRaccoon.Access;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     A refused memory_search query is the caller's own text and nothing else's: the log line and
///     the OTLP span carry policy, length and hash instead (SECURITY.md's "no search queries"), and
///     suppression alone cannot pass — the replacement diagnostic must be there.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ObservabilityCollection.Name)]
public sealed class RefusedQueryRedactionTests
{
    // The measured canary shape (security review): a machine-output query whose payload carries a secret.
    private const string CanarySecret = "hunter2";
    private static readonly string Canary =
        "[IMPORTANT: Background process LANEJ-REFUSE-CANARY completed normally (exit code 0).\n"
        + "Command: LANEJ-SECRET-ARG --password hunter2\n]";

    [Fact]
    public async Task RefusedQuery_IsAbsentFromTheSpan_WhileTheDiagnosticNamesPolicyLengthAndHash()
    {
        var metrics = new ToolCallMetrics();
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        var tools = CreateTools(new FakeStore());

        await Should.ThrowAsync<McpException>(() =>
            ToolCallRecorder.ThroughFilterAsync(metrics, "memory_search",
                ToolCallRecorder.Arguments(("projectId", "acme")),
                token => tools.Search("acme", Canary, sessionId: "sess-test", cancellationToken: token),
                TestContext.Current.CancellationToken));

        var activity = stopped.Single(a => a.OperationName == "tools/call memory_search");
        var spanText = SpanText(activity);
        spanText.ShouldNotContain(CanarySecret);
        spanText.ShouldNotContain("LANEJ-SECRET-ARG");
        spanText.ShouldNotContain("Background process");

        spanText.ShouldContain("MachineOutputQuery");
        spanText.ShouldContain($"{Canary.Length} chars");
        spanText.ShouldContain(ContentHash.OfValue(Canary));
    }

    [Fact]
    public async Task RefusedQuery_IsAbsentFromTheRefusalLog_WhileTheDiagnosticNamesPolicyLengthAndHash()
    {
        var fakeLogs = new FakeLoggerProvider();
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(new LoggerFactory([fakeLogs]))
            .BuildServiceProvider();
        var tools = CreateTools(new FakeStore());

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = async (_, token) =>
        {
            await tools.Search("acme", Canary, sessionId: "sess-test", cancellationToken: token);
            return new CallToolResult();
        };

        var result = await ToolRefusals.Filter(next)(Request("memory_search", services), CancellationToken.None);

        // The text stays in the caller's error result: it only ever travels back to its sender.
        TextOf(result).ShouldContain("Refused query:");

        var record = fakeLogs.Collector.GetSnapshot().Single(r => r.Message.Contains("refused:", StringComparison.Ordinal));
        record.Message.ShouldNotContain(CanarySecret);
        record.Message.ShouldNotContain("LANEJ-SECRET-ARG");
        record.Message.ShouldNotContain("Background process");

        record.Message.ShouldContain("MachineOutputQuery");
        record.Message.ShouldContain($"{Canary.Length} chars");
        record.Message.ShouldContain(ContentHash.OfValue(Canary));
    }

    [Fact]
    public async Task ShadowVerdict_LogsTheFingerprint_NotTheQuery()
    {
        var fakeLogs = new FakeLoggerProvider();
        var logger = new LoggerFactory([fakeLogs]).CreateLogger<MemoryTools>();
        var store = new FakeStore();
        store.Settings[QueryGuardConfigKeys.ShadowGlobal] = "true";
        var tools = CreateTools(store, logger);

        await tools.Search("acme", Canary, kind: "memory", sessionId: "sess-test",
            cancellationToken: TestContext.Current.CancellationToken);

        var record = fakeLogs.Collector.GetSnapshot().Single(r => r.Id.Id == 920);
        record.Message.ShouldNotContain(CanarySecret);
        record.Message.ShouldNotContain("LANEJ-SECRET-ARG");
        record.Message.ShouldNotContain("Background process");

        record.Message.ShouldContain("MachineOutputQuery");
        record.Message.ShouldContain($"{Canary.Length} chars");
        record.Message.ShouldContain(ContentHash.OfValue(Canary));
    }

    private static MemoryTools CreateTools(FakeStore store, ILogger<MemoryTools>? logger = null)
    {
        var access = new MemoryAccessGuard(store);
        var gate = new ToolGate(access, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard(), new StubMigrationGate(migrated: false));
        return new MemoryTools(store, gate,
            new SearchDispatcher(store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(store),
            new MemoryWriteService(store, new FakePromotionQueue()),
            new NoOpMeasurementRecorder(),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryTools>.Instance);
    }

    private static RequestContext<CallToolRequestParams> Request(string toolName, IServiceProvider services)
    {
        var server = Substitute.For<McpServer>();
        return new RequestContext<CallToolRequestParams>(server,
            new JsonRpcRequest { Method = "tools/call", Id = new RequestId("redact-1") },
            new CallToolRequestParams { Name = toolName })
        {
            Services = services
        };
    }

    /// <summary>Everything the span can carry off-machine: status text, tag values, and event tag values.</summary>
    private static string SpanText(Activity activity) =>
        string.Join("\n",
        [
            activity.StatusDescription ?? string.Empty,
            .. activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"),
            .. activity.Events.SelectMany(e => e.Tags ?? []).Select(tag => $"{tag.Key}={tag.Value}")
        ]);

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private sealed class FakeStore : FakeMemoryStore
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults([], SearchTimings.Empty));

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                Settings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
    }

    private sealed class StubMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }
}
