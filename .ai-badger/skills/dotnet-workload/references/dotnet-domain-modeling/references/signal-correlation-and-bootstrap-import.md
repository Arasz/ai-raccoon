# Signal Correlation & Bootstrap Import — Pattern & Test Recipes

Reference for implementing platform-specific signal correlation (matching inbound signals to existing cases) and bulk bootstrap import (seeding existing cases from platform data exports).

## When to Use

After implementing a platform signal parser, two additional domain concerns typically emerge:

1. **SignalCorrelator** — matching an inbound signal to an existing case/offer (three-tier waterfall).
2. **BootstrapImporter** — bulk-creating cases from a platform data export (e.g., a CSV snapshot export).

## File Layout

```
src/MyApp.Domain/ChannelMonitoring/{Platform}/
    {Platform}CorrelationResult.cs      # enum + result record
    {Platform}SignalCorrelator.cs       # static class: three-tier correlation
    {Platform}SnapshotRecord.cs         # value object for one export row
    {Platform}ImportResult.cs           # preview + result + error types
    {Platform}Importer.cs               # static class: DryRun + Import
```

## Three-Tier Signal Correlator

### Pattern

A static class that matches an inbound signal to existing data using a waterfall of increasingly fuzzy strategies:

1. **Exact match** — platform's stable ID (e.g., external ID) matches an existing offer's URL. Return the case linked to that offer.
2. **Fuzzy match** — company + title match (case-insensitive, trimmed). If exactly one candidate, return it.
3. **No match** — return null. Signal enters pipeline as `Proposed` with no correlation.

### Ambiguity Rule

When fuzzy matching produces **more than one** candidate:
- Return `None` (equivalent to null) — the correlator **never guesses**.
- The signal enters as `Proposed` with a descriptive summary.

### Method Signature

```csharp
public static class {Platform}SignalCorrelator
{
    public static {Platform}CorrelationResult Correlate(
        {Platform}Signal signal,
        IReadOnlyList<Case> activeCases,
        IReadOnlyList<Offer> allOffers);
}
```

### Result Types

```csharp
public enum {Platform}CorrelationMatchType { Exact, Fuzzy, Multiple, None }

public sealed record {Platform}CorrelationResult
{
    public required {Platform}CorrelationMatchType MatchType { get; init; }
    public string? CaseId { get; init; }  // null for None/Multiple
    public string? OfferId { get; init; }         // populated for Exact/Fuzzy
    public int CandidateCount { get; init; }      // 0 for None/Exact, N for Fuzzy/Multiple
}
```

### Test Matrix (8 tests minimum)

| Test | Input | Expected |
|---|---|---|
| Exact match with case | Signal RecordId matches offer URL + case exists | `Exact`, correct OfferId + CaseId |
| Exact match without case | Signal RecordId matches offer URL, no case | `Exact`, OfferId set, CaseId null |
| Fuzzy match (single candidate) | Company+title match one case | `Fuzzy`, correct CaseId |
| No match | Company+title don't match anything | `None`, all fields null |
| Multiple fuzzy matches | Company+title match two cases | `Multiple`, CandidateCount=2, CaseId null |
| Empty company | Signal with empty Company | `None` (no fuzzy match possible) |
| Empty lists | No offers, no cases | `None` |
| Case insensitivity | "ACME"/"dev" vs "acme"/"Dev" | `Fuzzy` match |

## Bootstrap Importer

### Pattern

A static class with two methods:
- **`DryRun`** — pure computation that classifies each snapshot record as Import/Skip/Proposal against existing data. NO writes.
- **`Import`** — creates Case + Offer records for accepted records, using delegate injection for domain purity.

### DryRun Classification Logic

For each record, evaluate a dedup chain:

1. **Offer URL dedup** — extract record ID from URL; if matching offer exists:
   - Existing case for that offer → `Skip` ("Already tracked")
   - Existing signal with matching ExternalId → `Skip` ("Already ingested")
2. **Fuzzy conflict detection** — company+title match existing case(s):
   - State beyond Submitted → `Skip` ("already in state")
   - Submitted with different date (>1 day) → `Proposal` ("different applied date")
   - Draft/Ready → `Proposal` ("manual review needed")
   - Multiple candidates → `Proposal` ("Multiple cases match")
3. **No match** → `Import`

### Preview/Result Types

```csharp
public enum {Platform}ImportAction { Import, Skip, Proposal }

public sealed record {Platform}ImportItemPreview
{
    public required int RecordIndex { get; init; }
    public required string Company { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
    public required {Platform}ImportAction Action { get; init; }
    public string? SkipReason { get; init; }
}

public sealed record {Platform}ImportPreview
{
    public required IReadOnlyList<{Platform}ImportItemPreview> Items { get; init; }
    public required int TotalRecords { get; init; }
    public required int WillImport { get; init; }
    public required int WillSkip { get; init; }
    public required int Proposals { get; init; }
}

public sealed record {Platform}ImportResult
{
    public required int Imported { get; init; }
    public required int Skipped { get; init; }
    public required IReadOnlyList<{Platform}ImportError> Errors { get; init; }
}
```

### Import Method (Delegate Injection)

Use `Func<T, T>` delegates for repository operations to keep the domain layer pure:

```csharp
public static {Platform}ImportResult Import(
    IReadOnlyList<{Platform}SnapshotRecord> acceptedRecords,
    string userId,
    Func<Offer, Offer> createOffer,
    Func<Case, Case> createCase)
```

Per-record try/catch: failures are recorded in `Errors`, not thrown. This allows partial imports to succeed.

### Case Factory Method

Add a static factory on `Case` for imported cases:

```csharp
public static Case Create{Platform}Import(
    string id, string userId, string offerId,
    DateTimeOffset appliedAt, DateTimeOffset createdAt) => new()
{
    Id = id, UserId = userId, OfferId = offerId,
    State = CaseState.Submitted,
    AppliedAt = appliedAt, CreatedAt = createdAt,
    StateHistory = [new StateHistoryEntry
    {
        FromState = CaseState.Draft,
        ToState = CaseState.Submitted,
        At = appliedAt,
        TriggeredBy = TransitionTrigger.System,
        Note = "Imported from {Platform}. Case was submitted via {Platform}."
    }]
};
```

Key properties: state is `Submitted` (already submitted), `AppliedAt` sourced from the platform data (not `DateTimeOffset.UtcNow`), `TriggeredBy = System`, single synthetic history entry.

### Test Matrix

#### DryRun tests (12 tests minimum)

| Test | Input | Expected |
|---|---|---|
| Imports all when no existing data | N records, empty existing | N `Import`, counts correct |
| Skips when offer+case exist (URL dedup) | Record URL matches existing offer+case | `Skip`, "Already tracked" |
| Skips when signal exists (signal dedup) | Record URL matches existing signal | `Skip`, "Already ingested" |
| Skips when case beyond Submitted | Fuzzy match to `InReview` state case | `Skip`, "already in state" |
| Proposes when case in Draft | Fuzzy match to `Draft` state case | `Proposal`, "manual review" |
| Proposes on multiple fuzzy matches | Company+title match 2 cases | `Proposal`, "Multiple" |
| Proposes on date mismatch | Submitted case with different date | `Proposal`, "different applied date" |
| Skips Submitted same date (via dedup) | URL with record ID matching existing offer+case in Submitted | `Skip` (dedup chain, NOT fuzzy) |
| Handles missing company+title | Empty Company/Title | `Import` (no fuzzy match possible) |
| Handles null URL | Null URL | `Import` (no exact match possible) |
| Empty preview on no records | Empty list | 0 items, 0 all counts |
| Deterministic | Same input twice | Identical output |

#### Import tests (4 tests minimum)

| Test | Input | Expected |
|---|---|---|
| Creates case and offer | One accepted record | Case at Submitted with history, Offer with correct Source |
| Reuses existing offer | Record matching existing offer | Case created referencing existing offer |
| Idempotent re-import | Run import, then dry-run same data | Second dry-run: all `Skip` |
| Records errors | Delegate that throws on 2nd call | Error recorded, other records succeed |

#### Case factory tests (6 tests minimum)

| Test | Assertion |
|---|---|
| State is Submitted | `State == Submitted` |
| AppliedAt set | `AppliedAt == appliedAt parameter` |
| Single history entry | `StateHistory.Count == 1`, correct from/to |
| TriggeredBy is System | `History[0].TriggeredBy == System` |
| No GeneratedCv | `GeneratedDocumentId == null` |
| No UsedDocument | `UsedDocument == null` |

## Pitfalls

| Pitfall | Fix |
|---|---|
| `SkipReason` is `string?` → Shouldly `ShouldContain` CS8604 | Use `SkipReason!.ShouldContain(...)` — the `!` null-forgiving operator satisfies the analyzer since the test assertion IS the null check |
| `Note` is `string?` → same CS8604 | Same fix: `Note!.ShouldContain(...)` |
| `ExtractRecordId` is `internal` but test project can't see it | Add `<InternalsVisibleTo Include="MyApp.Domain.Tests"/>` to the Domain `.csproj`. The project does NOT have this by default. |
| "Submitted same date" test fails if no URL with record ID | The dedup chain requires URL → record ID → existing offer → existing case. Without a URL, the fuzzy match finds the case but falls through (same date = no date mismatch, Submitted = not beyond Submitted, not Draft/Ready). Fix: use a record URL with record ID so the offer-URL dedup chain catches it. |
| Delegate-based Import method: how to test error recording | Use a mutable `callCount` counter in the delegate. On the Nth call, throw. Assert `result.Errors` has exactly 1 entry with the correct RecordIndex. |
| BootstrapImporter.Import creates new offers for every record | The "reuse existing offer" logic lives in the API/orchestration layer (dry-run detects duplicates, passes the existing offer ID). The domain Import method always creates offer + case per record. |
