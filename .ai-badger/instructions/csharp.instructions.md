---
description: 'C# and .NET conventions.'
applyTo: '**/*.cs,**/*.csproj,Directory.Build.props,Directory.Packages.props'
---

# C# and .NET

- Use nullable reference types and the C# language version configured by `Directory.Build.props`.
- Write a failing, behavior-focused xUnit test before each production behavior change. Use descriptive test names and a fluent assertion library (e.g. Shouldly).
- Use braces for every conditional and loop. Prefer `extension` members, explicit construction where the target type is not on the same line, and a guard-clause helper (e.g. `CommunityToolkit.Diagnostics`) for argument validation rather
  than hand-rolled `if (x is null) throw` blocks.
- Keep validators (e.g. FluentValidation) nested inside the validated type and use camel-case JSON property paths.
- Use nested source-generated `[LoggerMessage]` methods rather than direct `ILogger` calls.
- Keep NuGet versions centralized (e.g. `Directory.Packages.props`); never pin a `Version` on an individual `PackageReference`.
- Preserve the project's layering: keep the pure/domain layer infrastructure-free, keep a single writer to any shared datastore, and keep thin adapter projects (MCP, CLI) mapping to the core API without embedding business logic.
- Failed automated steps must preserve enough input context to retry or resume; silent failure handling is a defect.
- REST errors use problem details, [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) (which obsoleted RFC 7807) — register `AddProblemDetails()` rather than hand-rolling an error contract.
- Run `dotnet build` and `dotnet test` from the repository root after changes.
