# Compensation Refactoring — Domain Type Wrapping Session

## Context
A Polish compensation calculator refactor split flat domain types into composite types:
- `EmploymentCompensationCalculationProfile` lost B2B-specific fields (TaxForm, ZusScheme, etc.)
- New `B2BCompensationCalculationProfile` holds those B2B-specific fields
- New `CompensationProfile` wraps `B2BProfile?` and `EmploymentProfile?`
- `TakeHomeCalculator` removed, replaced by `CompensationCalculator(IB2BCompensationCalculator, IEmploymentCompensationCalculator)`
- `SalaryRecommendationEngine` now takes `(ICompensationCalculator, INegotiationHeuristics)`
- `Salary` replaced by `SalaryOffer { B2BOffer?, EmploymentOffer? }`
- `FakeTaxRateProvider.Rates` changed from `Dictionary<string, TaxYearRates>` to `Dictionary<int, TaxYearRates>`

## Error Categories (94 total)

| Category | Error Code | Count | Root Cause |
|---|---|---|---|
| TakeHomeCalculator removed | CS0246 | ~8 | Type removed, replaced by CompensationCalculator |
| EmploymentProfile missing B2B fields | CS0117 | ~20 | TaxForm/ZusScheme/VoluntarySickness/LumpSumRate/MonthlyBusinessCosts moved to B2BProfile |
| ContractType is read-only | CS0200 | ~8 | ContractType is now a computed property returning Employment/B2B |
| SaveAsync type mismatch | CS1503 | ~15 | Takes CompensationProfile now, not EmploymentCompensationCalculationProfile |
| GetEmploymentProfileAsync/GetB2BProfileAsync removed | CS1061 | ~10 | Replaced by GetProfileAsync |
| SalaryRecommendationEngine constructor | CS7036 | ~6 | Now requires INegotiationHeuristics as 2nd param |
| Recommend signature changed | CS7036 | ~8 | Now takes SalaryOffer, CompensationProfile, CompensationPreferences, TaxYearRates, ExtendedRecommendationParameters |
| SingleCompensationCalculationBreakdown removed | CS1061 | ~8 | Replaced by Breakdowns (IReadOnlyCollection<CalculationBreakdown>) |
| FakeTaxRateProvider string→int | CS1503 | ~5 | GetRates key changed from string to int |
| CompensationContractType.Mandate removed | CS0117 | ~2 | Only Employment and B2B remain |
| ShouldRoundTripEqual missing using | CS1061 | ~3 | Need `using Infrastructure.Tests.Support` |
| CalculationPerspective missing using | CS0103 | ~3 | Need `using ...Result` namespace |

## Fix Sequence (recommended order)

1. **ConfiguredTaxRateProviderTests** — Mechanical: `GetRates("2026")` → `GetRates(2026)`
2. **CompensationProfileRepositoryContract** — Change arg types from EmploymentProfile to CompensationProfile, GetB2BProfileAsync → GetProfileAsync
3. **CompensationProfileEncryptionTests** — Wrap profiles in CompensationProfile, add `using Support`
4. **TakeHomeCalculatorTests** — Use EmploymentCompensationCalculator/B2BCompensationCalculator directly
5. **NegotiationHeuristicsTests** — Static calls → instance calls, new engine constructor
6. **SalaryRecommendationRefuseTests** — Refuse now takes (CompensationCalculationResult, string)
7. **SalaryFunctionsTests** — Full rewrite of NewSut helper, profile helpers, request JSON shapes
8. **SalaryRecommendationPipelineTests** — Full rewrite using new types

## Remaining Behavioral Failures (14 tests)

After fixing all 94 compilation errors, 14 tests failed due to behavioral changes:
- Golden fixture values changed (19,232.98 → 19,532.98 for UoP 32k) — the OfferPov/ProfilePov split changes how parameters flow through the calculator
- API endpoints now require CompensationPreferences to be seeded before ComputeTakeHome/CompareContracts work
- Response JSON property paths changed (e.g., `outcome` → `employmentResult.offerPov.outcome`)

## Key Pitfalls Discovered

1. **Method overload ambiguity**: `OfferSalary(decimal)` returning `Salary` and `OfferSalary(decimal, bool)` returning `SalaryOffer` — C# picks the simpler overload, causing CS1503 when the call site expects SalaryOffer.

2. **Missing `using` for extension methods in Infrastructure.Tests**: `ShouldRoundTripEqual` lives in `JobSearchAiAssistant.Infrastructure.Tests.Support`, not in the standard Shouldly namespace.

3. **CompensationPreferences required by API**: The SalaryFunctions endpoint calls `compensationPreferencesRepository.GetAsync` before proceeding. Tests that only seed a CompensationProfile will get ResourceNotFoundException.
