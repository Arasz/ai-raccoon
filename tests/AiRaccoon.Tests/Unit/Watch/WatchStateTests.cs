using System.Text.Json;
using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>D4 states and the status record the tools surface.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchStateTests
{
    [Fact]
    public void State_HasExactlyTheFourContractStates() =>
        Enum.GetNames<WatchState>().ShouldBe(["Scanning", "Healthy", "Retrying", "Stopped"]);

    [Fact]
    public void State_SerializesAsString()
    {
        JsonSerializer.Serialize(WatchState.Scanning).ShouldBe("\"Scanning\"");
        JsonSerializer.Serialize(WatchState.Stopped).ShouldBe("\"Stopped\"");
    }

    [Fact]
    public void Status_CarriesAllFields()
    {
        var lastSync = DateTimeOffset.Parse("2026-08-04T10:00:00Z");
        var status = new WatchStatus("acme", "/repo", WatchState.Retrying, "boom", lastSync);

        status.ProjectId.ShouldBe("acme");
        status.Path.ShouldBe("/repo");
        status.State.ShouldBe(WatchState.Retrying);
        status.LastError.ShouldBe("boom");
        status.LastSync.ShouldBe(lastSync);
    }

    [Fact]
    public void Status_LastErrorAndLastSync_DefaultToNull()
    {
        var status = new WatchStatus("acme", "/repo", WatchState.Scanning);
        status.LastError.ShouldBeNull();
        status.LastSync.ShouldBeNull();
    }

    [Fact]
    public void Status_RoundTripsThroughJson()
    {
        var status = new WatchStatus("acme", "/repo", WatchState.Healthy, "err", DateTimeOffset.Parse("2026-08-04T10:00:00Z"));
        JsonSerializer.Deserialize<WatchStatus>(JsonSerializer.Serialize(status)).ShouldBe(status);
    }
}
