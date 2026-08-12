# JSAA Architecture Quality Gate — Distilled 2026-08-11

From full lens PeachPDF swap + CvTheme + Generic-CV 2-page pipeline + legal pages.

## Invariant Checklist

| Invariant | Check command | Pass? |
|-----------|---------------|-------|
| screaming architecture | folders by domain, not `Services/Controllers/Utils` | `Api/CvManagement/GeneratedCvs/Ai` — pass; `Documents/Pdf/Styles+Templates` is chassis exception |
| clean layering | `DomainPurityTests:41 GetReferencedAssemblies allowlist` + `Domain.csproj` no Azure/Cosmos/Http | pass — no PeachPDF in Domain |
| DI composition | `AddInfrastructure` + `AddApiServices` + `CompositionTests:136` singleton | concrete `GenericCvCondenser` without interface — SHOULD-FIX |
| static-classes pure only | `search static class` → body must not contain I/O | `CvHtmlBuilder:339 LoadResource` does I/O — MUST-FIX |
| guard clauses | `Guard.IsNotNull[_OrWhiteSpace]` | pass |
| LoggerMessage | `private static partial class Log` + `[LoggerMessage] EventId` | pass (`GenericCvGenerator.Log`, `LlmStepRetry`); pinned `CrossCuttingArchitectureTests:33` |
| bounded retry | `if (Exceeds) retry` not while + `LlmStepRetry.MaxAttempts=3` + `CreateTimer` + short-circuit | pass single if (`GenericCvGenerator:63`) |
| partition-by-userId + single-writer | `new PartitionKey(userId)` + only `Cosmos` ns holds `Container` (`CrossCuttingArchitectureTests:48`) | pass |
| managed identity | `DefaultAzureCredential` + `GetUserDelegationKeyAsync` | pass |
| no hardcoded secrets | emulator key only in test hosts | pass |

## Must-Fix Traps

1. **Sync-over-Async** `PeachPdfCvRenderer.cs:33 GetAwaiter().GetResult()` bridging sync Domain to async lib. Fix RenderAsync.
2. **Static I/O** `CvHtmlBuilder` extract to `ICvHtmlBuilder`.

## References

- Design: `docs/work/designs/2026-08-10-generic-cv-2-page-limit.md v2`
- Review: `docs/work/reviews/2026-08-11-integration-review-generic-cv-2-page.md`
