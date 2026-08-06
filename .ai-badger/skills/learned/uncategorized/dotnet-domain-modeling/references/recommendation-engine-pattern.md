# Recommendation Engine Pattern

When a domain feature needs to produce a recommendation from multiple deterministic
heuristics, with optional LLM prose framing but **never LLM-authored numbers**.

## Structure

```
NegotiationHeuristics (static)    ← pure functions, one per heuristic
    ↓
SalaryRecommendationEngine        ← orchestrates calculator + heuristics
    ↓
SalaryRecommendation (record)     ← output with rationale, source tag
```

### Static Heuristics Class

Each heuristic is a `public static` method operating on pre-computed domain results
(e.g. `TakeHomeResult`). Heuristics NEVER call the LLM — they extract, transform,
or combine deterministic figures.

```csharp
public static class NegotiationHeuristics
{
    // H1: Floor = monthly net of current compensation
    public static decimal ComputeFloor(TakeHomeResult currentCompTakeHome)
    {
        if (currentCompTakeHome.Outcome != TakeHomeOutcome.Computed
            || currentCompTakeHome.Breakdown is null)
            return 0m;
        return currentCompTakeHome.Breakdown.MonthlyNet;
    }

    // H2: Target = monthly net of offer
    public static decimal ComputeTarget(TakeHomeResult offerTakeHome) { ... }

    // H3: Stretch = target * (1 + premium/100)
    public static decimal ComputeStretch(TakeHomeResult offerTakeHome, decimal marketPremiumPercent) { ... }

    // H4: Leverage notes (textual, not numeric)
    public static string ComputeLeverageNotes(LeverageFactors factors) { ... }

    // H5: Risk adjustment percentage
    public static decimal ComputeRiskAdjustment(RiskFactors factors) { ... }
}
```

### Enum-Valued Risk Factors

Risk factors use enums (not strings) for testability and exhaustive switching:

```csharp
public enum CompanySize { Small, Medium, Large }
public enum FundingStage { Seed, SeriesA, Growth, Established }
public enum MarketCondition { Declining, Stable, Growing }

public sealed record RiskFactors
{
    public CompanySize CompanySize { get; init; } = CompanySize.Medium;
    public FundingStage FundingStage { get; init; } = FundingStage.Established;
    public MarketCondition MarketCondition { get; init; } = MarketCondition.Stable;
}
```

Each enum value maps to an additive adjustment percentage. Default values produce 0% total.
This makes the default-risk test trivial and edge cases easy to enumerate.

### Engine: Orchestrate + Clamp + Refuse

The engine takes a calculator (injected) and orchestrates the heuristics:

```csharp
public sealed class SalaryRecommendationEngine
{
    private readonly TakeHomeCalculator _calculator;

    public SalaryRecommendationEngine(TakeHomeCalculator calculator) { ... }

    public SalaryRecommendation Recommend(
        Salary offer, CompensationProfile profile, string taxYear, PlTaxYearRates rates,
        Salary? currentCompensation = null, CompensationProfile? currentProfile = null,
        LeverageFactors? leverageFactors = null, RiskFactors? riskFactors = null,
        decimal marketPremiumPercent = 10m)
    {
        // 1. Compute take-home for the offer via calculator
        var offerTakeHome = _calculator.Calculate(offer, profile, taxYear, rates);

        // 2. Refuse if calculator can't produce a figure
        if (offerTakeHome.Outcome != TakeHomeOutcome.Computed)
            return SalaryRecommendation.Refuse(offerTakeHome);

        // 3. Apply H1-H5 heuristics
        // 4. Clamp recommendation between floor and stretch
        // 5. Build rationale from deterministic figures
        // 6. Return with Source = RecommendationSource.Deterministic
    }
}
```

### Output Record with Source Tag

```csharp
public enum RecommendationSource { Deterministic, LlmFramed }

public sealed record SalaryRecommendation
{
    public required decimal RecommendedAmount { get; init; }
    public required string Rationale { get; init; }
    public required string LeverageNotes { get; init; }
    public required TakeHomeResult TakeHomeBreakdown { get; init; }
    public required RecommendationSource Source { get; init; }
    public bool IsRefused => TakeHomeBreakdown.Outcome != TakeHomeOutcome.Computed;

    public static SalaryRecommendation Refuse(TakeHomeResult failedResult) => new() { ... };
}
```

**The `Source` field is the NFR enforcement point.** Tests assert `Source == Deterministic`
to prove no LLM touched the figures. Even when LLM framing is added later, the numeric
source remains `Deterministic` — `LlmFramed` means "LLM wrote the prose, not the numbers."

## Testing Strategy

### Per-Heuristic Unit Tests (H1-H5)

Each heuristic gets 2+ tests with known inputs. Use pre-built `TakeHomeResult` fixtures
(avoid round-tripping through the full calculator for unit tests):

```csharp
private static TakeHomeResult ComputedResult(decimal monthlyNet) => new()
{
    Outcome = TakeHomeOutcome.Computed,
    RateTableVersion = "PL-2026.1",
    TaxYear = "2026",
    ContractType = CompensationContractType.Employment,
    Breakdown = new TakeHomeBreakdown { MonthlyNet = monthlyNet, /* ... */ }
};
```

### Engine Integration Tests

Use the real `TakeHomeCalculator` + `PolishCompProfile` + known rate table:

| Test | Asserts |
|------|---------|
| Source is always `Deterministic` | `rec.Source.ShouldBe(RecommendationSource.Deterministic)` |
| Rationale always non-empty | `rec.Rationale.ShouldNotBeNullOrWhiteSpace()` |
| Unsupported contract → refuse | `rec.IsRefused.ShouldBeTrue()`, `rec.RecommendedAmount.ShouldBe(0m)` |
| Different contract types → different amounts | B2B ryczałt > UoP for same gross |
| With current comp → floor in rationale | `rec.Rationale.ShouldContain("floor", Case.Insensitive)` |
| With risk factors → adjusts amount | High risk < no risk |
| Breakdown comes from calculator | `rec.TakeHomeBreakdown.RateTableVersion.ShouldBe("PL-2026.1")` |

### StepType + InterventionSource Tests

Verify enum/const members exist via `Enum.Parse` and direct const access:

```csharp
[Fact]
public void StepType_has_ComputeTakeHome()
{
    var value = Enum.Parse<StepType>("ComputeTakeHome");
    value.ToString().ShouldBe("ComputeTakeHome");
}
```

## Conventions

| Convention | Rationale |
|---|---|
| Static heuristics, not instance methods | No state, no DI — pure function signatures |
| Heuristics take pre-computed results | Calculator owns the math; heuristics own the strategy |
| Default risk factors → 0% adjustment | Makes "no risk info" a no-op, not a guess |
| `Refuse()` factory on output record | Explicit refusal > null or exception for unsupported inputs |
| Clamping between floor and stretch | Prevents recommendations below floor or above stretch |
| Rationale built from numbers, not templates | Each figure appears in the text — auditable |

## Pitfalls

| Pitfall | Fix |
|---|---|
| Heuristic calls calculator directly | Heuristic should receive pre-computed `TakeHomeResult` — engine orchestrates |
| LLM produces numeric figures | Enforce via `Source` field + test assertion; LLM fills prose slots only |
| Default risk factors produce non-zero adjustment | Design enum mappings so `(Medium, Established, Stable)` = 0% |
| Missing `IsRefused` check downstream | Always check `IsRefused` before using `RecommendedAmount` |
| Rationale doesn't mention key figures | Include floor, target, stretch, risk adjustment in rationale text |
