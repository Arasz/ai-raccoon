---
name: architecture-quality-review
description: Use when reviewing .NET architecture invariants.
version: 1.0.0
author: Hermes Agent
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [architecture, code-review, dotnet]
    related_skills: [code-review-checklist, comprehensive-code-review]
---

# Architecture Quality Review

Class-level architecture & code-quality lens for full diffs and module interactions.
Graded findings with file:line evidence, project-invariant checks.

## When to Use

- Architecture & code-quality lens since a date, or PeachPDF/CvTheme/GenericCv wave review
- Multi-layer .NET wave: Domain/Infrastructure/Api + Cosmos/Durable
- Pre-merge gate needs invariant verdict

## Checks

Screaming architecture, clean layering (DomainPurityTests allowlist), composition/DI
(AddInfrastructure/AddApiServices + lifetimes), static-classes (pure only),
guard clauses, LoggerMessage (nested static partial Log), bounded retry
(LlmStepRetry MaxAttempts + short-circuit), partition-by-userId + single-writer
(Container in Cosmos namespace only), managed identity, no hardcoded secrets.

## Traps (2026-08-11 wave)

- **Sync-over-Async:** `PeachPdfCvRenderer.cs:33 GetAwaiter().GetResult()` bridging sync
  `ICvPdfRenderer.Render` to async `PdfGenerator.GeneratePdf`. Fix: `RenderAsync`
  or context-free caller + ConfigureAwait(false).
- **Static I/O:** `CvHtmlBuilder:339 GetManifestResourceStream` in `static class` violates
  pure-only rule. Fix: `ICvHtmlBuilder` injectable.
- **DI Abstraction Bypass:** Concrete service instantiating inner components via `new Component(innerDefault)` in lazy fields or primary constructors instead of accepting DI-registered abstractions (e.g. `IFileTypeMatcher`). Fix: accept interface (`IFileTypeMatcher? matcher = null`) in constructor.
- **Property Allocation Leak:** Getter re-allocating collections (`.ToHashSet()`) on every access of an immutable wrapper instead of exposing a pre-computed `FrozenSet<T>`.

## Approach

1. Load invariants, ledger, design records.
2. `git diff --stat` + read changed spots.
3. Emit MUST-FIX/SHOULD-FIX/NIT with file:line + snippet + impact + fix.
4. Verdict B+/GOOD etc.

## References

- `references/architecture-quality-gate-jsaa.md`
- `references/infra-swap-traps.md`
