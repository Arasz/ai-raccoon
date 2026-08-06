# Financial / Tax Domain Modeling Patterns

Patterns for building deterministic tax, payroll, and financial calculators as pure domain services.

## Rate Table Pattern

All statutory values live in a versioned record keyed by year. No value may appear as a `const` or literal in any method.

```csharp
public sealed record PlTaxYearRates
{
    public required int TaxYear { get; init; }
    public required string Version { get; init; }      // "PL-2026.1"
    public required DateOnly EffectiveFrom { get; init; }
    public required DateOnly? EffectiveTo { get; init; }
    public required string SourceUrl { get; init; }     // provenance travels with data

    // All rates from the table, never hardcoded
    public required decimal MinimumWage { get; init; }
    public required ContributionRates Social { get; init; }
    public required decimal HealthRateGeneral { get; init; }
    // ... etc
}
```

**Why keyed by year:** Statutory rates change every January. A year-keyed table with a startup fail-fast check when the current year's table is missing enforces the annual maintenance obligation by the build, not by memory.

**Why no fallback:** Silently reusing last year's table produces a confidently wrong number. Refuse with `UnsupportedTaxYear` instead.

## Rounding Helpers

Polish payroll uses truncated rounding (toward zero) for grosze and zloty:

```csharp
private static decimal RoundGrosze(decimal value) =>
    Math.Round(value, 2, MidpointRounding.ToZero);

private static decimal RoundZloty(decimal value) =>
    Math.Floor(value);
```

**Pitfall:** `Math.Round(value, 2)` defaults to `MidpointRounding.ToEven` (banker's rounding), which rounds 94.3884 → 94.39 instead of 94.38. For financial calculations, always specify the rounding mode explicitly.

**Rounding stages:**
1. Social contributions → grosze (RoundGrosze)
2. Health contribution → grosze (RoundGrosze)
3. Tax base → full zloty (RoundZloty / Math.Floor)
4. Tax advance → full zloty (RoundZloty / Math.Floor)

## Annual-Average Pattern for Progressive Tax

When computing monthly take-home with annual thresholds (e.g. 12%/32% PIT at 120k PLN):

```csharp
// Compute everything annually first
var annualGross = grossMonthly * 12m;
var annualSocial = /* annual ZUS with 30× cap */;
var annualHealth = /* annual health */;
var annualPit = CalculateProgressivePit(annualTaxBase, rates);

// Derive monthly average
var monthlyNet = (annualGross - annualSocial - annualHealth - annualPit) / 12m;
```

**Why annual-first:** The 30× pension cap and the 12%→32% threshold crossing both depend on cumulative annual income. Computing month-by-month is more accurate but requires tracking cumulative state. Annual-average gives a deterministic, reproducible result.

**Trade-off:** Monthly net varies throughout the year (higher after the cap/threshold is hit). The annual average hides this variation. For V1, this is acceptable; add month-by-month projection as a follow-up.

## Interface Design: Calculator vs. Entry Point

Split validation from computation:

```csharp
// Entry point — validates, delegates, stamps provenance
public sealed class TakeHomeCalculator(ICountryCompProfile countryProfile)
{
    public TakeHomeResult Calculate(Salary offer, CompensationProfile profile,
        string taxYear, PlTaxYearRates rates)
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
        string taxYear, PlTaxYearRates rates);
}
```

**Why separate:** The entry point handles cross-cutting concerns (validation, provenance stamping). The country profile is a pure function of the inputs. One implementation per country.

## B2B/JDG: Entrepreneur Pays Full ZUS Rate

For sole proprietors (JDG/B2B), there is no employer/employee split — the entrepreneur pays the **combined** rate:

```csharp
// ❌ Wrong: only employee portion
var pension = social.PensionEmployee * zusBase;

// ✅ Correct: full rate (employee + employer combined)
var pension = (social.PensionEmployee + social.PensionEmployer) * zusBase;
```

The `ContributionRates` record carries both splits (for UoP where they differ), but B2B sums them. No FGŚP for JDG.

## Rate Table Provenance Test

Prove that no statutory value is hardcoded by modifying a rate and verifying the result changes:

```csharp
[Fact]
public void All_rates_come_from_the_table_not_hardcoded()
{
    var (calc, _) = CreateSut();
    var modifiedRates = CreatePl2026Rates() with
    {
        Social = CreatePl2026Rates().Social with { PensionEmployee = 0.10m }
    };
    var result = calc.Calculate(offer, profile, "2026", modifiedRates);
    result.Breakdown!.MonthlyNet.ShouldNotBe(GOLDEN_FIXTURE_NET);
}
```

## Factory Method for Rate Tables in Tests

Create a factory method with the full rate table, annotated with source citations:

```csharp
/// <summary>
/// 2026 Polish statutory rates, sourced from:
/// - Dz.U. 2025 poz. 1242 (minimum wage)
/// - M.P. 2025 poz. 1206 (forecast average wage, 30× cap)
/// - podatki.gov.pl PIT thresholds
/// See docs/research/beta-epics/spike-199-salary-calculator.md §4
/// </summary>
private static PlTaxYearRates CreatePl2026Rates() => new()
{
    TaxYear = 2026,
    Version = "PL-2026.1",
    // ... all values with source citations in comments
};
```
