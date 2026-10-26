using AiRaccoon.Core.Decisions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Decisions;

[Trait("Speed", "Fast")]
public sealed class DecisionContentTests
{
    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Confidence_must_be_a_finite_probability(double confidence) =>
        Should.Throw<ArgumentException>(() => new DecisionContent("title", "architecture", "scenario", "outcome", "reason", confidence: confidence));
    [Theory]
    [InlineData("title", 201)]
    [InlineData("category", 101)]
    [InlineData("scenario", 16001)]
    [InlineData("outcome", 16001)]
    [InlineData("reasoning", 16001)]
    public void Required_fields_refuse_blank_and_oversized_values(string field, int length)
    {
        DecisionContent Create(string value) => new(
            field == "title" ? value : "title", field == "category" ? value : "category",
            field == "scenario" ? value : "scenario", field == "outcome" ? value : "outcome",
            field == "reasoning" ? value : "reasoning");
        Should.Throw<ArgumentException>(() => Create(" "));
        Should.Throw<ArgumentException>(() => Create(new string('x', length)));
    }

    [Fact]
    public void Validity_end_must_follow_start_and_dates_are_normalized()
    {
        var start = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(2));
        Should.Throw<ArgumentException>(() => new DecisionContent("t", "c", "s", "o", "r", validFrom: start, validUntil: start));
        var content = new DecisionContent("t", "c", "s", "o", "r", validFrom: start, validUntil: start.AddHours(1));
        content.ValidFrom!.Value.Offset.ShouldBe(TimeSpan.Zero);
        content.ValidFrom.Value.Hour.ShouldBe(10);
        content.ValidUntil!.Value.Hour.ShouldBe(11);
    }

    [Fact]
    public void Evidence_is_copied_and_bounded()
    {
        var alternatives = new[] { "first" };
        var sources = new[] { new DecisionSource("docs/adr/a.md", "source") };
        var content = new DecisionContent("t", "c", "s", "o", "r", alternatives: alternatives, sources: sources);
        alternatives[0] = "changed";
        sources[0] = new DecisionSource("other", "changed");
        content.Alternatives.ShouldBe(new[] { "first" });
        content.Sources.Single().Uri.ShouldBe("docs/adr/a.md");
        Should.Throw<ArgumentException>(() => new DecisionContent("t", "c", "s", "o", "r", alternatives: Enumerable.Repeat("x", 21).ToArray()));
        Should.Throw<ArgumentException>(() => new DecisionContent("t", "c", "s", "o", "r", consequences: [new string('x', 2001)]));
        Should.Throw<ArgumentException>(() => new DecisionContent("t", "c", "s", "o", "r", sources: [new DecisionSource(" ", "source")]));
    }
    [Fact]
    public void Source_count_accepts_twenty_and_refuses_twenty_one()
    {
        var source = new DecisionSource("docs/adr/a.md", "source");
        var content = new DecisionContent("t", "c", "s", "o", "r", sources: Enumerable.Repeat(source, 20).ToArray());
        content.Sources.Count.ShouldBe(20);
        Should.Throw<ArgumentException>(() => new DecisionContent("t", "c", "s", "o", "r", sources: Enumerable.Repeat(source, 21).ToArray()));
    }

    [Fact]
    public void Returned_evidence_collections_cannot_be_modified()
    {
        var content = new DecisionContent("t", "c", "s", "o", "r", alternatives: ["a"], consequences: ["c"], sources: [new DecisionSource("source", "why")]);
        Should.Throw<NotSupportedException>(() => ((IList<string>)content.Alternatives)[0] = "changed");
        Should.Throw<NotSupportedException>(() => ((IList<string>)content.Consequences)[0] = "changed");
        Should.Throw<NotSupportedException>(() => ((IList<DecisionSource>)content.Sources)[0] = new DecisionSource("other", "other"));
        content.Alternatives.Single().ShouldBe("a");
        content.Consequences.Single().ShouldBe("c");
        content.Sources.Single().Uri.ShouldBe("source");
    }
}
