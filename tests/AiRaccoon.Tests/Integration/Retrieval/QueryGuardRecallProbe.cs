using AiRaccoon.Core.Memory.QueryGuard;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     What does the read-path guard (ADR-0040) actually catch, measured against real recorded
///     traffic rather than against the fixtures it was built from?
///     <para>
///         The occasion: a 407-token query reached the embedder, and sampling the long queries in
///         `search_quality` showed <b>every one of them is pasted machine output</b> — HTTP header
///         dumps, log errors, test output — which is exactly what the guard exists to refuse. The
///         guard is enabled by default and not in shadow mode, yet those queries were recorded,
///         which only happens after it declines to refuse.
///     </para>
///     <para>
///         Reports; asserts nothing about the product. Length is used only to SELECT a population to
///         look at — it is not a label, and the printout below is what a human labels from.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class QueryGuardRecallProbe
{
    internal const string BankEnvVar = "AIRACCOON_QUERYGUARD_RECALL_BANK";

    private readonly ITestOutputHelper _output;

    public QueryGuardRecallProbe(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Probe_RunsTheGuardOverRecordedQueries()
    {
        var bank = Environment.GetEnvironmentVariable(BankEnvVar);
        if (bank is null)
        {
            _output.WriteLine($"{BankEnvVar} not set — measures the query guard against recorded search traffic.");
            return;
        }

        File.Exists(bank).ShouldBeTrue($"{BankEnvVar} must name an existing bank file");

        await using var connection = new SqliteConnection($"Data Source={bank};Mode=ReadOnly");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        List<string> queries =
        [
            .. await connection.QueryAsync<string>("SELECT query FROM search_quality WHERE query IS NOT NULL")
        ];
        queries.Count.ShouldBeGreaterThan(0, "the probe needs recorded traffic to mean anything");

        var verdicts = queries
            .Select(q => (Query: q, Tier: QueryGuardPolicy.Evaluate(q).Tier))
            .ToList();

        _output.WriteLine($"queries recorded : {verdicts.Count}");
        foreach (var tier in new[] { QueryGuardTier.Refuse, QueryGuardTier.Warn, QueryGuardTier.Clean })
        {
            _output.WriteLine($"  {tier,-7}        : {verdicts.Count(v => v.Tier == tier)}");
        }

        // The population that motivated this: long queries. Length selects them; it does not label
        // them — a long query CAN be a genuine question, and the printout is what says otherwise.
        var longOnes = verdicts.Where(v => v.Query.Length > 1200).ToList();
        _output.WriteLine($"queries over 1200 chars: {longOnes.Count}");
        foreach (var tier in new[] { QueryGuardTier.Refuse, QueryGuardTier.Warn, QueryGuardTier.Clean })
        {
            var n = longOnes.Count(v => v.Tier == tier);
            _output.WriteLine($"  {tier,-7}        : {n} ({(longOnes.Count == 0 ? 0 : 100.0 * n / longOnes.Count):F0}%)");
        }

        _output.WriteLine("--- longest queries the guard called Clean, first line only ---");
        foreach (var v in longOnes.Where(v => v.Tier == QueryGuardTier.Clean)
                     .OrderByDescending(v => v.Query.Length).Take(8))
        {
            var firstLine = v.Query.Split('\n')[0];
            _output.WriteLine($"  {v.Query.Length,7} chars | {firstLine[..Math.Min(90, firstLine.Length)]}");
        }
    }
}
