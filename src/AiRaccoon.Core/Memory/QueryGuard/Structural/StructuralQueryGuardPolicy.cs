namespace AiRaccoon.Core.Memory.QueryGuard.Structural;

/// <summary>
///     The structural detector's read-path verdict (docs/adr/0041) — a third input to the warn
///     tier, alongside <see cref="QueryGuardPolicy" />'s two regex tiers. Pure and dependency-free
///     like <see cref="QueryGuardPolicy" />: no embedding, no I/O, the threshold supplied by the
///     caller rather than baked in here (F8/the record's threshold-transfer finding — calibrate on
///     the deployment's own traffic, see <see cref="StructuralNoiseCalibrator" />).
///     <para>
///     Never returns <see cref="QueryGuardTier.Refuse" />: F9 measured this detector's false
///     positives landing on genuinely valuable content (measurement tables, citation blocks), so
///     unlike the regex refuse tier (22/22 graded matches scored 2/5, never higher) a structural
///     match here is not evidence-backed enough to block a search outright.
///     </para>
/// </summary>
public static class StructuralQueryGuardPolicy
{
    public const string PolicyName = "StructuralShapeScore";

    public const string Guidance =
        "This query's shape (line layout, punctuation density, token structure) statistically resembles tool output rather than a question. Memory search matches meaning, not literal text — if you're not intentionally searching for this exact fragment, try rephrasing as a question about what you want to find.";

    public static QueryGuardVerdict Evaluate(string query, double threshold)
    {
        ArgumentNullException.ThrowIfNull(query);

        var score = StructuralNoiseModel.Score(StructuralNoiseFeatures.Extract(query));
        return score >= threshold ? QueryGuardVerdict.Warn(PolicyName, Guidance) : QueryGuardVerdict.Clean;
    }
}
