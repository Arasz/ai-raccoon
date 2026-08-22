using AiRaccoon.Core.Memory.QueryGuard;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard;

/// <summary>
///     Pure string-detection tests for the read-path query guard (docs/adr/0040). Every refuse/warn
///     fixture below is captured verbatim from the live bank's search_quality table (read-only,
///     2026-08-14) — not invented strings — per the project's own post-mortem on tuning noise
///     filters against imagined input.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class QueryGuardPolicyTests
{
    // search_quality id 173 (project ai-raccoon): one of 12 graded queries matching this shape,
    // all scored 2/5, never higher.
    private const string RealHermesProcessNotification =
        """
        [IMPORTANT: Background process proc_97aa3ea5eb50 completed normally (exit code 0).
        Command: cd /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/fix-after-refactor && dotnet test --no-build --filter "Category=Unit" --logger "console;verbosity=detailed" > /tmp/unit_run.log 2>&1; echo "EXIT: $?" >> /tmp/unit_run.log
        Output:
        ]
        """;

    // search_quality id 150 (truncated): one of 10 graded queries matching this shape, all scored
    // 2/5, never higher.
    private const string RealAsyncDelegationBatch =
        """
        [ASYNC DELEGATION BATCH COMPLETE — deleg_f91b8f3d]
        A background fan-out of 2 subagent(s) you dispatched earlier has finished. All ran in parallel and waited on each other; their consolidated results are below. You may have moved on since dispatching
        """;

    // search_quality id 251 (truncated): a real pasted .NET exception, graded 1/5.
    private const string RealStackTracePaste =
        """
        log-leak

         System.Net.Http.HttpClient.ServerProbe.ClientHandler[104]
              HTTP request failed after 5.0227ms
              System.Net.Http.HttpRequestException: Connection refused (127.0.0.1:7721)
               ---> System.Net.Sockets.SocketException (61): Connection refused
                 at System.Net.Sockets.Socket.AwaitableSocketAsyncEventArgs.ThrowException(SocketError error, CancellationToken cancellationToken)
        info: System.Net.Http.HttpClient.ServerProbe.LogicalHandler[100]
              Start processing HTTP request POST http://127.0.0.1:7721/mcp
        """;

    // Every graded-4-and-5 query in search_quality that is not a process-notification, captured
    // verbatim (2026-08-14). The false-positive rate this policy produces against real high-value
    // traffic — the number the task asks to be measured and reported.
    public static TheoryData<string> RealGrade4And5Queries { get; } = new(
    [
        "memory source normalization spike results",
        "- is the hook called?\n- can we check the queue, promote what looks good and clean up rest?",
        "observe git console for git push results from lefthook",
        "den-refresh ai-badger scaffold command",
        "e: lets test the hook by doing at least 20 memory searches, try to do at least 5 about shared facts",
        "shared tier promotion queue hygiene",
        "memory source normalization schema v5",
        "FTS5 external content triggers source_file section",
        "Prometheus auto-grading rubric calibration",
        "watch replace by path digest scan lease",
        "workspace sandbox consolidate discard isolation",
        "shared tier promotion criteria durable facts sweep exempt",
        "memory_share idempotent value-addressed per-chunk sharing",
        "search quality table correlation follow-through grading",
        "ai-badger scaffold welcome den-refresh plugin",
        "hermes cron job scheduled watchdog",
        "debug core reason behaind failing tests, implement high quality fix (you should research best aproach)",
        "deploy smoke",
        "lets den-refresh ai-raccoon"
    ]);

    [Fact]
    public void Evaluate_WithRealHermesProcessNotification_Refuses()
    {
        var verdict = QueryGuardPolicy.Evaluate(RealHermesProcessNotification);

        verdict.Tier.ShouldBe(QueryGuardTier.Refuse);
        verdict.PolicyName.ShouldBe(QueryGuardPolicy.RefusePolicyName);
        verdict.Guidance.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Evaluate_WithRealAsyncDelegationBatchNotification_Refuses()
    {
        var verdict = QueryGuardPolicy.Evaluate(RealAsyncDelegationBatch);

        verdict.Tier.ShouldBe(QueryGuardTier.Refuse);
        verdict.PolicyName.ShouldBe(QueryGuardPolicy.RefusePolicyName);
    }

    [Fact]
    public void Evaluate_WithLeadingWhitespaceBeforeTheHermesPrefix_StillRefuses()
    {
        var verdict = QueryGuardPolicy.Evaluate("   " + RealHermesProcessNotification);

        verdict.Tier.ShouldBe(QueryGuardTier.Refuse);
    }

    [Fact]
    public void Evaluate_WithRealStackTracePaste_Warns()
    {
        var verdict = QueryGuardPolicy.Evaluate(RealStackTracePaste);

        verdict.Tier.ShouldBe(QueryGuardTier.Warn);
        verdict.PolicyName.ShouldBe(QueryGuardPolicy.WarnPolicyName);
        verdict.Guidance.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Evaluate_WithAConsoleLogLevelPrefixLine_Warns()
    {
        var verdict = QueryGuardPolicy.Evaluate("some earlier line\ninfo: something happened here");

        verdict.Tier.ShouldBe(QueryGuardTier.Warn);
    }

    [Theory]
    [InlineData("why did the auth build start failing")]
    [InlineData("promotion queue eviction policy")]
    [InlineData("what is the retry backoff for ServerProbe")]
    public void Evaluate_WithAnOrdinaryQuestion_IsClean(string query)
    {
        QueryGuardPolicy.Evaluate(query).Tier.ShouldBe(QueryGuardTier.Clean);
    }

    [Theory]
    [MemberData(nameof(RealGrade4And5Queries))]
    public void Evaluate_WithARealHighGradedQuery_IsClean(string query)
    {
        // The measured false-positive rate this test proves: 0 / 19 real graded-4-and-5 queries
        // trip either the refuse or the warn tier.
        QueryGuardPolicy.Evaluate(query).Tier.ShouldBe(QueryGuardTier.Clean);
    }

    [Fact]
    public void Evaluate_WithNullQuery_Throws()
    {
        Should.Throw<ArgumentNullException>(() => QueryGuardPolicy.Evaluate(null!));
    }

    /// <summary>
    ///     Zero embedding cost is guaranteed by construction (QueryGuardPolicy takes no dependencies
    ///     — it cannot call an embedder). This measures the string/regex cost directly: ADR-0029
    ///     measured real embedding at 14.6-26.5 ms; this must land far below 1 ms per call.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Performance, TestCategories.Benchmark)]
    public void Evaluate_RunsInSubMillisecondTime()
    {
        const int iterations = 10_000;
        var queries = new[] { RealHermesProcessNotification, RealStackTracePaste, "why did the auth build start failing" };

        // Warm up JIT before measuring.
        foreach (var q in queries)
        {
            QueryGuardPolicy.Evaluate(q);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            QueryGuardPolicy.Evaluate(queries[i % queries.Length]);
        }

        stopwatch.Stop();
        var averageMs = stopwatch.Elapsed.TotalMilliseconds / iterations;
        averageMs.ShouldBeLessThan(1.0);
    }
}
