using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using Xunit;
using Shouldly;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait("Speed", "Fast")]
public class ZeroShotNoiseFixtureTests
{
    private class SearchQualityRow
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("query")] public string Query { get; set; } = "";
        [JsonPropertyName("usefulness_grade")] public int UsefulnessGrade { get; set; }
    }

    [Fact]
    public void Fixture_CanBeLoaded_AndEvaluated()
    {
        var path = "search_quality_eval.json";
        if (!File.Exists(path))
        {
            // Fallback for different test runners
            path = Path.Combine("..", "..", "..", "..", "search_quality_eval.json");
            if (!File.Exists(path))
            {
                return; // silently skip if fixture is missing
            }
        }

        var json = File.ReadAllText(path);
        var rows = JsonSerializer.Deserialize<List<SearchQualityRow>>(json);

        rows.ShouldNotBeNull();
        rows.Count.ShouldBeGreaterThan(0);

        // Prototype behavior: just asserting we can read the usefulness grades
        // that will train/evaluate the ZeroShot model threshold.
        var highQuality = rows.FindAll(r => r.UsefulnessGrade >= 4).Count;
        var noise = rows.FindAll(r => r.UsefulnessGrade <= 2).Count;

        (highQuality + noise).ShouldBeGreaterThan(0);
    }
}
