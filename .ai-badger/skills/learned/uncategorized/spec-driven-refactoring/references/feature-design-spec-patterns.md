# Feature Design Spec Patterns (DDD / .NET)

Patterns for writing feature design specifications in Domain-Driven Design projects.
Captured from successful spec-writing sessions (e.g., issue #187 — LinkedIn signal correlation).

## Procedure

1. **Read existing project docs** — `requirements.md`, `functional-specification.md`, `architecture.md`,
   `data-model.md` for cross-cutting conventions.
2. **Read existing feature specs** — `docs/features/*/spec-*.md` for section structure, requirement ID
   format, and level of detail. The project's own specs are the canonical format source.
3. **Read the feature README** — `docs/features/README.md` defines required dossier files
   (`requirements.md`, `flows.md`, `specification.md`, `integration.md`).
4. **Read domain code** — aggregates, entities, value objects, repository interfaces, state machines.
   Note existing patterns (factory methods, discriminated unions, sealed records).
5. **Read research/spike docs** — `docs/research/` for verified findings, access paths, data schemas.
   Ground claims in these; don't invent.
6. **Write the spec** following the project's established structure.

## Section Structure

Observed in production specs (spec-issue-177.md, spec-issue-196.md):

```
1. Header        — Status, Epic (link), Issue (link), Domain namespace, Dependencies
2. Scope         — In scope (domain layer only), out of scope (infrastructure, UI, orchestration)
3. Requirements  — FR-{PREFIX}-N.N format with behavior tables and rationale
4. Domain types  — New types (C# signatures), existing types (table), helpers
5. Deduplication — Keys, fallbacks, idempotence contracts (if applicable)
6. Edge cases    — Table: scenario → behavior
7. Testing       — Unit tests (table: test case | input | expected), integration tests
8. Acceptance    — Given/When/Then, numbered AC-1, AC-2, …
9. File inventory — New files table, modified files table
10. Impl order   — Phased, dependencies, TDD-first
```

## Requirement ID Conventions

- Feature-scoped prefix: `FR-LI-3.1`, `FR-CM-3.2`, `FR-GM-6.1`
- Prefix = 2–3 letter abbreviation of the feature/epic name
- Format: `FR-{PREFIX}-{MAJOR}.{MINOR}` — major groups, minor enumerates
- Non-functional: `NFR-{PREFIX}-{N}`
- Each requirement has a short title and `[V1]` / `[V2]` version tag

## Domain Type Documentation Patterns

### New types — full C# signatures
```csharp
/// <summary>XML doc comment explaining purpose.</summary>
public sealed record TypeName
{
    public required string Id { get; init; }
    // ...
}
```
Include complete type definitions (records, enums, interfaces, static classes).
For complex logic, include algorithm pseudocode alongside the signature.

### Existing types — table only
| Type | File | Role |
|------|------|------|
| `Application` | `Applications/Application.cs` | Aggregate; receives new factory method only |

No code for unchanged types — just the table.

### Helpers — signatures + normalization rules
Static utility classes with method signatures and documented normalization/transformation rules.

## Testing Strategy Patterns

### Unit tests — table format
| Test case | Input | Expected result |
|-----------|-------|-----------------|
| **Exact match — job id** | Signal with `JobId = "12345"`, one offer… | `CorrelationMatch` with `MatchTier.Exact` |

Cover at minimum: happy path, no-match, multiple-match, dedup/re-import, edge inputs (nulls, empty).

### Integration tests — end-to-end flows
Cover the full pipeline from input to persisted state. Reference the orchestration issue
for deferred integration tests.

## Acceptance Criteria Patterns

Given/When/Then, numbered:

```markdown
### AC-1: Short title
**Given** [precondition],
**when** [action],
**then** [expected outcome].
```

Each AC maps to one or more FR requirements. Must be testable without implementation details.

## Pitfalls

- **Don't assume format.** Always read existing specs first — conventions vary by project.
- **Ground in research.** Cite spike docs and verified findings. Don't invent claims.
- **Keep domain types pure.** Repository interfaces in Domain; implementations in Infrastructure.
  No `Azure.*`, `HttpClient`, or infrastructure packages in the Domain project.
- **Ambiguity as proposals, never guesses.** When matching is uncertain, produce proposal
  records for user resolution rather than auto-resolving.
- **Never mutate existing aggregates on import.** Bootstrap/import operations should only
  create new entities or skip. Conflicts surface as proposals.
- **Rider MCP `create_new_file` uses `text` not `newText`.** The parameter for file content
  is `text`. Using the wrong name creates an empty file silently.
- **Batch-read codebase files via `execute_code`** for efficiency when understanding a large
  codebase — loop over `read_file` calls in a single script rather than one tool call per file.
