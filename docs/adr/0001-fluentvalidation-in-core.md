# 0001 - FluentValidation for domain request validation

Date: 2026-08-03

Status: Accepted

## Context

`AiRaccoon.Core` defines the request models that cross the MCP boundary
(`SearchQuery`, `MemoryWriteRequest`). Their validation rules lived as
constructor guards using `CommunityToolkit.Diagnostics`, which:

- mixes range/whitespace rules into object construction, so the same rules
  cannot be reused for boundary-level validation reporting;
- gives no structured error output (property path + message) that the MCP
  layer could surface to a client;
- duplicates what `FluentValidation` — already referenced by the host
  project — is designed to do.

The project convention (`.ai-badger/instructions/csharp.instructions.md`)
states validators live nested inside the validated type with camel-case
property paths.

## Decision

Add `FluentValidation` as a dependency of `AiRaccoon.Core` and move the
validation rules for `SearchQuery` and `MemoryWriteRequest` into nested
`Validator : AbstractValidator<T>` classes:

- The record constructors become plain data assignment (no guards).
- The MCP tool boundary (`MemoryTools`) constructs the request, then calls
  `ValidateAndThrow` before delegating to the store — the only production
  construction site of these records.
- Property paths in validation errors are camelCase (matching the JSON tool
  arguments), set process-wide via a `ModuleInitializer`.
- Argument guards for internally-constructed/helper types (`Workspace`,
  `ContextNaming`, `RatingPolicy`) stay on `CommunityToolkit.Diagnostics`.

## Consequences

- Validation rules are single-sourced in the validated type and reusable at
  any boundary.
- Invalid requests surface as `FluentValidation.ValidationException` with
  structured errors instead of ad-hoc `ArgumentException`s.
- `AiRaccoon.Core` gains one third-party dependency (pure logic, no I/O);
  the clean-layering rule requires this ADR for that change.
- Constructor guards are gone, so constructing an invalid record no longer
  throws by itself — validation is the boundary's responsibility.
