using AiRaccoon.Observability;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Docs;

/// <summary>
///     The documents that show the counters command each carry the meter list by hand. The list
///     itself lives in one place (OtlpNames.Meters), so a meter added there reaches the printed
///     command at once and the documents a moment later — this test is what makes the gap
///     visible instead of leaving a document that names a meter the server no longer exports.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ObservabilityCountersCommandDocsTests
{
    private static readonly string[] Documents =
    [
        "docs/reference/cli-reference.md",
        "docs/reference/agent-memory-server.md",
        "docs/adr/0008-live-pid-discovery-for-monitoring.md",
        "docs/how-to/monitor-and-export-telemetry.md"
    ];

    [Theory]
    [InlineData("docs/reference/cli-reference.md")]
    [InlineData("docs/reference/agent-memory-server.md")]
    [InlineData("docs/adr/0008-live-pid-discovery-for-monitoring.md")]
    [InlineData("docs/how-to/monitor-and-export-telemetry.md")]
    public void Document_NamesEveryRegisteredMeter(string document)
    {
        var text = File.ReadAllText(TestData.RepoFile(document));

        foreach (var meter in OtlpNames.Meters)
        {
            text.ShouldContain(meter, customMessage:
                $"{document} shows the counters command but never names {meter}; the list it prints comes from OtlpNames.Meters");
        }
    }

    /// <summary>The list as one value, so a document cannot name the meters and still get the command wrong.</summary>
    [Theory]
    [InlineData("docs/reference/cli-reference.md")]
    [InlineData("docs/reference/agent-memory-server.md")]
    [InlineData("docs/how-to/monitor-and-export-telemetry.md")]
    public void Document_ShowsTheCountersValueTheToolPrints(string document)
    {
        var text = File.ReadAllText(TestData.RepoFile(document));

        text.ShouldContain(string.Join(',', OtlpNames.Meters), customMessage:
            $"{document} must show the --counters value the tool prints, which is OtlpNames.Meters joined");
    }

    /// <summary>No document may still tell the reader to add the flag by hand: the tool prints it.</summary>
    [Theory]
    [InlineData("docs/reference/cli-reference.md")]
    [InlineData("docs/reference/agent-memory-server.md")]
    [InlineData("docs/adr/0008-live-pid-discovery-for-monitoring.md")]
    [InlineData("docs/how-to/monitor-and-export-telemetry.md")]
    public void Document_DoesNotAskTheReaderToAppendTheFlag(string document)
    {
        var text = File.ReadAllText(TestData.RepoFile(document));

        text.ShouldNotContain("append `--counters", customMessage:
            $"{document} still asks the reader to add the --counters flag by hand; the printed command carries it");
    }
}
