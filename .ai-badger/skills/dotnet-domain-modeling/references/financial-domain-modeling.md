# Financial / Tax Domain Modeling Patterns

Patterns for building deterministic tax, payroll, and financial calculators as pure domain services.

## Rate Table Pattern

All statutory values live in a versioned record keyed by effective period. No value may appear as a `const` or literal in any method.

```csharp
public sealed record TaxYearRates
{
    public required int TaxYear { get; init; }
    public required string Version { get; init; }      // e.g. "2026.1"
    public required DateOnly EffectiveFrom { get; init; }
    public required DateOnly? EffectiveTo { get; init; }
    public required string SourceUrl { get; init; }     // provenance travels with data

    // All rates from the table, never hardcoded
    public required decimal MinimumWage { get; init; }
    public required ContributionRates Social { get; init; }
    public required decimal HealthContributionRate { get; init; }
    // ... etc, one property per statutory figure your jurisdiction publishes
}
```

**Why keyed by period:** Statutory rates change on a fixed cycle (often annually). A period-keyed table with a startup fail-fast check when the current period's table is missing enforces the maintenance obligation by the build, not by memory.

**Why no fallback:** Silently reusing the previous period's table produces a confidently wrong number. Refuse with a typed error (e.g. `UnsupportedTaxYear`) instead.

## Rounding Helpers

Payroll math typically needs two rounding rules: one for the minor currency unit (cents) and one for the major unit (whole currency), and they aren't always the same rule.

```csharp
private static decimal RoundToMinorUnit(decimal value) =>
    Math.Round(value, 2, MidpointRounding.ToZero);

private static decimal RoundToMajorUnit(decimal value) =>
    Math.Floor(value);
```

**Pitfall:** `Math.Round(value, 2)` defaults to `MidpointRounding.ToEven` (banker's rounding). At an exact midpoint this differs from "normal" rounding: `Math.Round(2.005m, 2)` gives `2.00` (rounds to the nearest even digit), while `Math.Round(2.005m, 2, MidpointRounding.AwayFromZero)` gives `2.01`. For financial calculations, always specify the rounding mode explicitly — don't rely on the framework default matching what your regulator requires.

**Rounding stages (example ordering):**
1. Contribution A → minor unit
2. Contribution B → minor unit
3. Tax base → major unit
4. Tax advance → major unit

Confirm the actual stage-by-stage rounding order against your jurisdiction's rules — it is not always "round once at the end."

## Annual-Average Pattern for Progressive Tax

When computing a periodic take-home figure under annual thresholds (example: a two-bracket progressive tax with a threshold — tune the bracket rates and threshold to your jurisdiction):

```csharp
// Compute everything annually first
var annualGross = grossMonthly * 12m;
var annualSocial = /* annual social contribution, honoring any cumulative-income cap */;
var annualHealth = /* annual health contribution */;
var annualTax = CalculateProgressiveTax(annualTaxBase, rates);

// Derive monthly average
var monthlyNet = (annualGross - annualSocial - annualHealth - annualTax) / 12m;
```

**Why annual-first:** A contribution cap that scales with cumulative annual income, and a bracket-threshold crossing, both depend on cumulative annual income. Computing month-by-month is more accurate but requires tracking cumulative state. Annual-average gives a deterministic, reproducible result.

**Trade-off:** The monthly figure varies throughout the year (higher after a cap or threshold is hit). The annual average hides this variation. For a first version this is often acceptable; add month-by-month projection as a follow-up if it isn't.

## Interface Design: Calculator vs. Entry Point

Split validation from computation:

```csharp
// Entry point — validates, delegates, stamps provenance
public sealed class TakeHomeCalculator(ICountryCompProfile countryProfile)
{
    public TakeHomeResult Calculate(Salary offer, CompensationProfile profile,
        string taxYear, TaxYearRates rates)
    {
        // Validate tax year matches rates
        // Validate contract type is supported
        // Delegate to countryProfile.Calculate(...)
        // Result already has version + tax year stamped by implementation
    }
}

// Country implementation — pure computation
public interface ICountryCompProfile
{
    string Country { get; }
    TakeHomeResult Calculate(Salary offer, CompensationProfile profile,
        string taxYear, TaxYearRates rates);
}
```

**Why separate:** The entry point handles cross-cutting concerns (validation, provenance stamping). The country profile is a pure function of the inputs. One implementation per country.

## Self-Employed: Individual Pays the Combined Contribution Rate

For sole proprietors / self-employed contractors, there is often no employer/employee split — the individual pays the **combined** rate that an employment relationship would otherwise split:

```csharp
// ❌ Wrong: only the employee portion
var pension = social.PensionEmployee * contributionBase;

// ✅ Correct: full rate (employee + employer combined)
var pension = (social.PensionEmployee + social.PensionEmployer) * contributionBase;
```

The `ContributionRates` record carries both splits (needed for standard employment where they differ), but the self-employed path sums them. Check whether your jurisdiction also drops any employer-only levies (e.g. a guarantee-fund contribution) for this category — don't assume the self-employed rate is a simple sum without verifying against the statute.

## Rate Table Provenance Test

Prove that no statutory value is hardcoded by modifying a rate and verifying the result changes:

```csharp
[Fact]
public void All_rates_come_from_the_table_not_hardcoded()
{
    var (calc, _) = CreateSut();
    var modifiedRates = CreateExampleRates() with
    {
        Social = CreateExampleRates().Social with { PensionEmployee = 0.10m }
    };
    var result = calc.Calculate(offer, profile, "2026", modifiedRates);
    result.Breakdown!.MonthlyNet.ShouldNotBe(GOLDEN_FIXTURE_NET);
}
```

## Factory Method for Rate Tables in Tests

Create a factory method with the full rate table, annotated with source citations — cite the actual statute/regulation and publication for each figure so a future maintainer can re-verify it:

```csharp
/// <summary>
/// Example statutory rates for tax year 2026 — placeholder values only.
/// Replace with your jurisdiction's real published figures and cite each source:
/// - the statute or regulation that sets the minimum wage
/// - the official publication that sets the contribution-cap multiplier
/// - the tax authority's published bracket thresholds
/// </summary>
private static TaxYearRates CreateExampleRates() => new()
{
    TaxYear = 2026,
    Version = "2026.1",
    // ... all values with source citations in comments
};
```
