using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Decisions;

public sealed record DecisionSource(string Uri, string Description);

/// <summary>The immutable rationale and evidence attached to one decision version.</summary>
public sealed record DecisionContent
{
    public DecisionContent(string title, string category, string scenario, string outcome, string reasoning,
        double? confidence = null, string? maker = null, IReadOnlyList<string>? alternatives = null,
        IReadOnlyList<string>? consequences = null, IReadOnlyList<DecisionSource>? sources = null,
        DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null)
    {
        DecisionText.Required(title, 200, nameof(title));
        DecisionText.Required(category, 100, nameof(category));
        DecisionText.Required(scenario, 16000, nameof(scenario));
        DecisionText.Required(outcome, 16000, nameof(outcome));
        DecisionText.Required(reasoning, 16000, nameof(reasoning));
        if (confidence.HasValue)
        {
            Guard.IsTrue(double.IsFinite(confidence.Value) && confidence.Value is >= 0 and <= 1, nameof(confidence));
        }

        if (maker is not null)
        {
            DecisionText.Identifier(maker, nameof(maker));
        }

        if (validFrom.HasValue && validUntil.HasValue)
        {
            Guard.IsTrue(validUntil.Value > validFrom.Value, nameof(validUntil));
        }

        var evidence = sources?.ToArray() ?? [];
        Guard.HasSizeLessThanOrEqualTo(evidence, 20, nameof(sources));
        foreach (var source in evidence)
        {
            Guard.IsNotNull(source, nameof(sources));
            DecisionText.Required(source.Uri, 2048, nameof(sources));
            DecisionText.Required(source.Description, 500, nameof(sources));
        }

        Title = title;
        Category = category;
        Scenario = scenario;
        Outcome = outcome;
        Reasoning = reasoning;
        Confidence = confidence;
        Maker = maker;
        Alternatives = CopyItems(alternatives, nameof(alternatives));
        Consequences = CopyItems(consequences, nameof(consequences));
        Sources = Array.AsReadOnly(evidence);
        ValidFrom = validFrom?.ToUniversalTime();
        ValidUntil = validUntil?.ToUniversalTime();
    }

    public string Title { get; }
    public string Category { get; }
    public string Scenario { get; }
    public string Outcome { get; }
    public string Reasoning { get; }
    public double? Confidence { get; }
    public string? Maker { get; }
    public IReadOnlyList<string> Alternatives { get; }
    public IReadOnlyList<string> Consequences { get; }
    public IReadOnlyList<DecisionSource> Sources { get; }
    public DateTimeOffset? ValidFrom { get; }
    public DateTimeOffset? ValidUntil { get; }

    private static IReadOnlyList<string> CopyItems(IReadOnlyList<string>? values, string parameter)
    {
        var copy = values?.ToArray() ?? [];
        Guard.HasSizeLessThanOrEqualTo(copy, 20, parameter);
        foreach (var item in copy)
        {
            DecisionText.Required(item, 2000, parameter);
        }

        return Array.AsReadOnly(copy);
    }
}
