namespace AiRaccoon.Core.Decisions;

/// <summary>The stable refusal kinds a decision command can raise; adapters map them to wire errors.</summary>
public static class DecisionErrorKinds
{
    public const string NotFound = "decision-not-found";
    public const string Conflict = "decision-conflict";
    public const string RequestConflict = "decision-request-conflict";
    public const string InvalidTransition = "decision-invalid-transition";
    public const string InvalidLink = "decision-invalid-link";
    public const string ExportTooLarge = "decision-export-too-large";

    /// <summary>Every kind above, read from the constants so the list cannot drift from them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        .. typeof(DecisionErrorKinds).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
    ];
}
